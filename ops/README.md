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
  like PSA's June 2026 one, decrease = census shrank). A real restatement hits
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
fetched a page and refused it, 504 the visit outran `Scraper:ExpressTimeoutSeconds`
(it still finishes on its own), 404 unknown card, 409 not-a-card.

Deployment deltas: **none**. Same binary, same systemd unit; no firewall or
`pg_hba.conf` change (loopback never leaves the box); no new grants — the worker's
`pokemon_app` role already holds `UPDATE ON cards`, which covers the new
`refresh_requested_at` column, and sibling apps speak HTTP to the worker, never SQL
to its tables. The one migration (`AddCardRefreshRequestedAt`) applies with the usual
owner-role `dotnet ef database update`.
