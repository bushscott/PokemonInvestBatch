# Dev machine, once per clone

```bash
git config core.hooksPath ops/git-hooks   # blocks committing appsettings.Production.json
```

# New Relic (as deployed 2026-07-28)

- Worker → OTLP via `NewRelic:LicenseKey` in appsettings.Production.json (US endpoint otlp.nr-data.net).
- Host: `newrelic-infra` via NR apt repo; `/etc/newrelic-infra.yml` (license_key, display_name).
- Postgres: `pg_stat_statements` in shared_preload_libraries + `CREATE EXTENSION`; role `newrelic` with `pg_monitor`; `nri-postgresql` config in `/etc/newrelic-infra/integrations.d/` with ENABLE_QUERY_MONITORING.
- Logs: `/etc/newrelic-infra/logging.d/pokemon.yml` forwards the worker's systemd unit + postgres log.
- **Pi 5 gotcha:** NR's fluent-bit crashes on the default 16K-page kernel
  (`jemalloc: Unsupported system page size`). Fix: `kernel=kernel8.img` in
  /boot/firmware/config.txt (4K pages) + reboot.
- Dashboard: `ops/dashboard.json`. To import: replace every `"accountIds": [0]`
  with your account id (it's in the one.newrelic.com URL), then Dashboards →
  Import dashboard → paste. Row 3 needs the StatsLane gauges (deployed with the
  worker); Row 6 widgets die gracefully — delete them if they show no data.
- Integrity metrics: `crawl.monotonicity_violations` (single hits are market
  noise — alert on a step change in the rate, the signature of a silent tier
  remap) and `crawl.pop_anomalies` by grader/kind (spike = census restatement
  like PSA's June 2026 one, decrease = a cell lost ≥20% and ≥2 cards at once;
  shedding a slab or two to cracking and regrading is normal and stays silent,
  which is why this tile is not chronically yellow). A real restatement hits
  hundreds of cards in one sweep; suggested condition:
  `SELECT sum(`crawl.pop_anomalies`) FROM Metric` above ~20 for 30 min sliding.

# Pi setup

Target: 16GB Raspberry Pi 5, 64-bit Raspberry Pi OS (Debian 12/13), SSD.

## 1. Postgres (apt)

```bash
sudo apt-get update && sudo apt-get install -y postgresql
```

## 2. Network access (LAN only)

Edit `/etc/postgresql/*/main/postgresql.conf`:

```
listen_addresses = '*'
```

Edit `/etc/postgresql/*/main/pg_hba.conf` — add ONE line, scoped to your LAN
subnet (adjust `192.168.1.0/24` to yours; never use `0.0.0.0/0`):

```
host    all    all    192.168.1.0/24    scram-sha-256
```

Then: `sudo systemctl restart postgresql`

## 3. Roles and databases

Edit the three passwords in `postgres-setup.sql`, then:

```bash
sudo -u postgres psql -f postgres-setup.sql
```

## 4. Schema

From the dev machine (owner role holds DDL; the app role never does):

```bash
POKEMON_DB="Host=<pi-ip>;Database=pokemon;Username=pokemon_owner;Password=..." \
  dotnet ef database update -p src/PokemonInvestBatch.Infrastructure -s src/PokemonInvestBatch.Infrastructure
```

Then apply the post-migration grants (the bottom of `postgres-setup.sql` says which tables and why):

```bash
sudo -u postgres psql -d pokemon -c "GRANT UPDATE ON cards, fingerprints, sets TO pokemon_app;"
```

The Pokédex tables (ADR-0011) take the same treatment once their migration has landed:

```bash
sudo -u postgres psql -d pokemon -c "
GRANT UPDATE ON species, species_types, species_egg_groups, species_names,
    card_tagging, set_details TO pokemon_app;
GRANT UPDATE, DELETE ON card_species TO pokemon_app;
GRANT DELETE ON species_types, species_egg_groups, species_names TO pokemon_app; -- re-import child replacement
"
```

These seven tables are derived, rebuildable current-state, not observations, so the
append-only posture that keeps `pokemon_app` off `DELETE` everywhere else in this
schema doesn't hold for them (ADR-0011 items 5–6). `cardstock_app`'s `SELECT` on all
seven needs no new grant here — ADR-0011 counts on it arriving through the existing
default privileges. Verify that on deploy rather than assume it: §8's acceptance
queries end with the read-check.

## 5. App deployment

Self-contained publish — no runtime installed on the Pi:

```bash
dotnet publish src/PokemonInvestBatch.Worker -c Release -r linux-arm64 --self-contained
```

systemd unit and connection config land with the Worker task.

## 6. The intake API (ADR-0006)

The worker hosts a loopback-only HTTP listener for sibling apps on the Pi — refresh
requests (queued) and express visits (immediate, synchronous). Port comes from
`Scraper:IntakePort` (default **5155**, `appsettings.Production.json`); the bind address
stays `127.0.0.1` unless you have a reason it shouldn't.

Smoke and debugging over SSH:

```bash
curl localhost:5155/healthz                                 # -> ok
curl -X POST localhost:5155/cards/630417/refresh-request    # 202: queued at the ask tier
curl -X POST localhost:5155/cards/630417/express-visit      # blocks until the visit commits
ss -ltn | grep 5155                                         # confirm the bind (loopback only)
```

Express responses: 200 parsed (fresh rows committed), 502 the site failed us, 422 we
fetched a page and refused it, 404 unknown card, 409 not-a-card, 500 the visit threw —
the body carries the exception, and there is no retry behind it (ADR-0008). An express
visit waits for nothing and fetches as soon as it is asked, in parallel with any other
express visit, so the volume the site sees is whatever the calling app sends. A slow
site is capped by the HTTP client's own 60 s timeout and comes back as a 502.

Deployment deltas: **none**. Same binary, same systemd unit; no firewall or
`pg_hba.conf` change (loopback never leaves the box); no new grants — the worker's
`pokemon_app` role already holds `UPDATE ON cards`, which covers the new
`refresh_requested_at` column, and sibling apps speak HTTP to the worker, never SQL
to its tables. The one migration (`AddCardRefreshRequestedAt`) applies with the usual
owner-role `dotnet ef database update`.

## 7. Sale-history continuity

A **gap** is a grade bucket whose page rolled past us between visits: sales happened,
the bucket filled, and the oldest rows scrolled off before we looked again. They are
unrecoverable — the site shows only the newest rows per bucket, and the paid API sells
prices, never sale history. Run the audit after any bug that could have starved the
crawl.

```bash
# Read-only. Safe against live prod, writes nothing.
ssh scott@<pi-ip> "cd /tmp && sudo -u postgres psql -d pokemon -f -" < ops/sales-gap-audit.sql

# Destructive. Trims each gapped card back to its latest gap so what survives is
# continuous. Writes /tmp/sales-cut.csv first as the only undo.
ssh scott@<pi-ip> "cd /tmp && sudo -u postgres psql -d pokemon -v ON_ERROR_STOP=1 -f -" < ops/sales-gap-cut.sql
```

Read the audit before running the cut, and dry-run the cut by substituting `ROLLBACK`
for its final `COMMIT` — it prints the full cut list and row counts either way.

The cut runs as the **owner** role: `pokemon_app` has no `DELETE` grant anywhere and
that restriction stays (§3). Only `sales` is ever touched — `price_months` and
`populations` are change-only writes whose absence means "checked, unchanged", and the
site's chart restates full monthly history on every visit, so they self-heal across any
outage and were never discontinuous.

First run, 2026-08-10: 5 gaps on 4 cards (Snorlax #76 twice, Mega Charizard X EX #23,
Psyduck #226, Mega Gengar ex #269 — every one the PSA 10 bucket), 1,290 rows cut,
rollback CSV at `/home/scott/sales-cut-20260810.csv` on the Pi.

## 8. Pokédex operations (ADR-0011)

`species`, `card_species`, `card_tagging` and `set_details` are re-derived by the
tagging lane on every sweep, not hand-maintained — but two things still need an
operator: overriding a wrong or missing verdict by hand, and confirming a sweep did
what ADR-0011 promises.

**Manual overrides.** A card's species link (or lack of one) can be pinned by hand,
the same posture ADR-0002 established for `delisted_at`: a human runs a documented
statement, the tagging lane honours the result but never writes `Manual` rows itself
and never overwrites one. The raw integers below are `TagStatus` (`Tagged = 0`,
`NoSpecies = 1`, `Quarantined = 2`) and `TagMethod` (`TitleMatch = 0`, `Manual = 1`).
Run these as `pokemon_app` once §4's grants land, or as `pokemon_owner` before then
(the owner role always holds them — it owns every table). They also assume the card
has already been swept at least once: on a never-swept card there is no existing
`card_tagging` row yet, so the `UPDATE` below is a 0-row no-op and the machine tags
the card fresh on its own next sweep regardless of which statement ran.

```sql
-- Pin a card's species by hand (survives every sweep):
INSERT INTO card_species (card_id, species_id, method) VALUES (<card>, <dex>, 1)
    ON CONFLICT (card_id, species_id) DO UPDATE SET method = 1;
UPDATE card_tagging SET status = 0, method = 1, updated_at = now() WHERE card_id = <card>;
-- Declare a card species-less by hand:
DELETE FROM card_species WHERE card_id = <card> AND method = 0;
UPDATE card_tagging SET status = 1, method = 1, updated_at = now() WHERE card_id = <card>;
-- Reverse a pin: remove the manual rows; the next sweep re-tags the card fresh:
DELETE FROM card_species WHERE card_id = <card> AND method = 1;
DELETE FROM card_tagging WHERE card_id = <card>;
```

Deleting the `card_tagging` row is what does the work: it returns the card to the
work set, and the next sweep re-examines it from scratch.

A `method = 1` (`Manual`) row freezes the card: the tagging lane's work set skips it
entirely on every future sweep, even after the card's title changes, until an
operator reverses the pin with another hand-run statement.

**Era mapping.** `set_details.era` comes from `tcgdex-series-eras.json` (named by the
`TcgdexSeriesEraPath` option; tracked in the repo and copied to the Pi by the deploy
recipe `publish.sh` prints, same posture as `tcgdex-set-aliases.json`). The key is
TCGdex's serie display name, character-for-character — a near-miss silently yields a
null era with the row still `Matched`. The sweep re-reads the file and re-resolves
era on **every** row every run, so adding an era is: add the key, deploy the file,
and either wait for the next sweep (24h tick) or `sudo systemctl restart
pokemon-invest-batch` to sweep now. A missing file is not an error — every era goes
null on the next sweep — which is why the file must stay in the deploy copy list.

**The Japanese shelf (ADR-0012).** Japanese sets map through exactly one path:
`tcgdex-ja-set-aliases.json` (PriceCharting slug → TCGdex ja set id, with a `reason`
per row). There is no name matching for Japanese and never will be — Japanese script
folds to nothing in the name normalizer, so a name-driven join could only produce
wrong-but-plausible data. The ja mirror lives beside the English one
(`tcgdex-mirror-ja/`, one directory per locale) and both stay fresh by themselves:
every sweep re-reads the one-page set list and downloads only documents the
directory lacks (the top-up), so a newly released TCGdex set arrives within a day
while pinned documents never change. Deleting a mirror directory is still the full
re-pin.

The ongoing curation loop, when new sets appear:

1. The sweep receipt log's per-shelf split (`sets N matched, M pending (English …,
   Japanese …)`) or CardStock's "awaiting metadata" wall says a pending count rose.
2. List the unaliased Japanese sets (query below) and find each one's TCGdex ja id
   in `tcgdex-mirror-ja/sets/` (id, Japanese name, release date, card counts).
3. Add a row per set to `tcgdex-ja-set-aliases.json` — the `reason` should carry the
   evidence (name translation, release date, count agreement). **When unsure, leave
   it unaliased**: a near-miss writes wrong-but-plausible data, and unaliased is an
   honest verdict, not a failure. A set with no TCGdex counterpart (promo pools,
   Bandai/Carddass products, DP- and BW-era sets TCGdex ja does not carry) simply
   stays pending.
4. If a new serie name appears, add its era key to `tcgdex-series-eras.json` —
   ordinal-exact, copied byte-for-byte from the mirror document, mapped to an
   existing era code so the shelves merge downstream.
5. Deploy the json files (they ride the `publish.sh` copy line) and wait for the
   next sweep or `sudo systemctl restart pokemon-invest-batch`.

```sql
-- Unaliased pending Japanese sets, the curation worksheet:
SELECT s.slug, s.name, count(c.id) FILTER (WHERE c.delisted_at IS NULL AND c.gone_at IS NULL) AS live_cards
FROM sets s
LEFT JOIN set_details d ON d.set_id = s.id AND d.match_status = 0
LEFT JOIN cards c ON c.set_id = s.id
WHERE d.set_id IS NULL AND s.slug LIKE 'pokemon-japanese-%'
GROUP BY s.slug, s.name ORDER BY live_cards DESC;
```

**Acceptance queries.** Re-run these any time after a sweep to confirm the lane did
what ADR-0011 promises. All read-only, safe against live prod.

1. Invariants — every taggable card has exactly one `card_tagging` row, and every
   set has exactly one `set_details` row (ADR-0011 item 1: "always," never absence).
   Both counts should read 0.
2. Coverage splits — how the corpus split across `Tagged`/`NoSpecies`/`Quarantined`,
   how sets split across `Matched`/`Pending`, and how matched sets split across eras
   (every era in `tcgdex-series-eras.json` should appear; a mapped era suddenly
   reading 0 means the file on the Pi lost a key).
3. 100-card eyeball sample — a random card → status → matched-species cross-section,
   for a human to skim.
4. Full quarantine list — every card the matcher refused to guess on (four or more
   candidate species), for manual review.
5. Species completeness, a Character-page smoke test, and the `cardstock_app` read
   check — the `species` table's row count; a plausible (not zero, not one)
   `card_species` link count for a known species (Umbreon, dex 197); and
   confirmation, run as `cardstock_app`, that the default-privileges `SELECT` noted
   in §4 actually reads.

```sql
-- 1. Invariants (expect 0 and 0):
SELECT count(*) FROM cards c LEFT JOIN card_tagging t ON t.card_id = c.id
    WHERE c.not_a_card_at IS NULL AND t.card_id IS NULL;
SELECT count(*) FROM sets s LEFT JOIN set_details d ON d.set_id = s.id WHERE d.set_id IS NULL;
-- 2. Coverage splits (report verbatim):
SELECT status, count(*) FROM card_tagging GROUP BY status ORDER BY status;
SELECT match_status, count(*) FROM set_details GROUP BY match_status;
SELECT era, count(*) FROM set_details GROUP BY era ORDER BY era;
-- 2b. The same split by language shelf (mirrors SetMapper.PartitionOf):
SELECT CASE
         WHEN s.slug LIKE 'pokemon-japanese%' THEN 'japanese'
         WHEN s.slug LIKE 'pokemon-chinese%'  THEN 'chinese'
         WHEN s.slug LIKE 'pokemon-korean%'   THEN 'korean'
         WHEN s.slug LIKE '%topps%'           THEN 'topps'
         ELSE 'english' END AS shelf,
       d.match_status, count(*)
FROM sets s JOIN set_details d ON d.set_id = s.id
GROUP BY 1, 2 ORDER BY 1, 2;
-- 3. 100-card eyeball sample (owner reviews):
SELECT c.name, t.status, string_agg(s.name, ' · ') FROM card_tagging t
    JOIN cards c ON c.id = t.card_id
    LEFT JOIN card_species cs ON cs.card_id = t.card_id LEFT JOIN species s ON s.id = cs.species_id
    GROUP BY c.id, c.name, t.status ORDER BY random() LIMIT 100;
-- 3b. Full quarantine list (owner reviews):
SELECT c.id, c.name FROM card_tagging t JOIN cards c ON c.id = t.card_id WHERE t.status = 2;
-- 4. Species completeness + icon gaps (icon gaps come from the lane's log line):
SELECT count(*) FROM species;
-- 5. Character-page smoke (expect Umbreon's printings, > 20 rows):
SELECT count(*) FROM card_species WHERE species_id = 197;
-- cardstock_app read check (run as cardstock_app):
SELECT count(*) FROM species;
```

**First deploy.** The first sweep after the migration and §4 grants land
self-bootstraps: a one-time PokéAPI dataset mirror fetch (~2,900 small files) and
icon fetch (~1,025 files), then a full backfill across the ~91k active cards, in
chunked transactions, taking minutes. Every sweep after that is incremental — it
only re-examines cards with no tagging row yet or a name that has drifted from what
was last tagged — and is usually a no-op.
