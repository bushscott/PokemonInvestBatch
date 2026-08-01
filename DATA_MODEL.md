# Data Model — what we store and how the data works

This document is the authoritative reference for every piece of data PokemonInvestBatch
holds: where each field comes from, how it behaves over time, what can be derived from it,
and — just as important — what data does **not** exist and can never be backfilled. It is
written to be ingested cold, by a human or an AI session, with no access to the codebase.

**The system in one paragraph:** a headless .NET 10 batch worker on a Raspberry Pi that
politely scrapes pricecharting.com for Pokemon card price history, individual sales, graded
population census, and product images, into Postgres (database `pokemon`, EF Core,
snake_case tables) plus a filesystem image store. It has **no HTTP API of its own** — data
flows one way, site → parsers → Postgres. The intended consumer is a future web app that
hunts undervalued cards; today the only readers are the worker's own scheduler and a New
Relic dashboard of *operational* metrics (row counts, delays, failures — never the market
data itself).

> ## ⚠️ Disclosure: this is the data-gathering schema, not the final product schema
>
> Everything documented below is how the schema exists **today**, and today it serves
> exactly one job: **gathering data** — collecting the facts from the source completely,
> politely, and irreversibly. It is *not* the finished data model of the product. When the
> web application is built, this schema is **expected to grow** with whatever its features
> demand: read-oriented views or projections, new derived/aggregate tables, user-facing
> entities (accounts, watchlists, valuations, annotations), and indexes shaped by query
> patterns that don't exist yet. Treat every "nobody reads this yet" and "no consumer
> exists" note in this document as a statement about the present, not a design ceiling.
> What will *not* change is the foundation: the fact tables and the three rules in §1 —
> future features build on top of the ledger, never by mutating it.

Conventions everywhere: timestamps are UTC `timestamptz`; money is integer **cents, always
USD** (the parser hard-asserts the site's currency dropdown reads USD before trusting a
number); card ids are PriceCharting's own product ids, never locally generated; set slugs
are stored verbatim-encoded exactly as the site's hrefs carry them (`champion%27s-path`).

---

## 1. Three rules that explain almost everything

Read these first; every table's behavior follows from them.

### Rule 1 — Facts are append-only, and the database enforces it

Fact rows are inserted, never edited, never deleted. This is not a code convention that a
bug could violate: the application's Postgres role (`pokemon_app`) has **no DELETE grant on
any table** and UPDATE on exactly three tables (`cards`, `sets`, `shapes` — see Rule 3).
A bug cannot destroy sales history; the role would reject the SQL.

### Rule 2 — Change-only append: a row is written only when the value changed

For the two observational histories (`price_months`, `populations`), each visit compares
what the page shows against the last stored observation and inserts **only the cells that
differ** ("never observed before" counts as zero). Consequences for anyone querying:

- **Latest value** = the row with the greatest `observed_at` for that key
  (`ORDER BY observed_at DESC LIMIT 1`), *not* the only row and *not* the newest month.
- **Absence of rows means "always zero"**, not "unknown". A PSA-grade-3 cell with no rows
  has been zero at every observation. (Whether we *looked* is answered by the `visits`
  table, which exists precisely because change-only storage can't distinguish "checked,
  unchanged" from "never checked".)
- **History between two stored rows is flat.** If grade-9 population has rows at March=120
  and July=124, the value was 120 for the entire gap — that's the storage contract, not a
  sampling artifact.
- Unchanged facts are never re-written, so row counts measure *change*, not crawl activity.

### Rule 3 — Facts vs. working memory: know which one you're reading

A handful of columns are **mutable scheduler state**, overwritten in place on every visit.
They are caches — every one is exactly derivable from the fact tables — and the future web
app should never treat them as facts:

| Mutable column | What it is | Derive the real thing from |
|---|---|---|
| `cards.last_visited_at` | when the scheduler last checked the card | `visits` (full history) |
| `cards.observed_sales_per_day` | churn speedometer: sale rows in trailing 30 days ÷ 30, at last visit | `sales.sold_on` (§6 — the derived series is *more* accurate than the cached value ever was) |
| `cards.any_bucket_at_cap` | "sales provably rolled off unseen" flag, currently | partially from `sales.captured_at` clustering; flip *moments* are alerted but not durably stored — known gap (§8) |
| `cards.failure_streak`, `cards.quarantined_until` | retry-queue bookkeeping | `visits.outcome` history |
| `sets.last_seen_at`, `sets.last_walked_at`, `shapes.last_seen_at` | re-observation bookkeeping | — (bookkeeping only) |

Everything else in the database is a fact and is immutable once written.

---

## 2. The source, and the hard limits of what it offers

All data comes from pricecharting.com (site facts verified 2026-07-27 against captured
pages spanning 2024→live). A card detail page (`/game/{set}/{card}`) contains exactly four
data assets:

1. **`VGPC.chart_data`** — the price chart: **six series of monthly average prices**, in
   cents, reaching back to ~December 2020. Series keys map to grade tiers *by the page's
   own tab labels*: `used`=Ungraded, `cib`=Grade 7, `new`=Grade 8, `graded`=Grade 9,
   `boxonly`=Grade 9.5, `manualonly`=PSA 10. (Trap for anyone touching the site: set pages
   reuse the same CSS class names for *different* grades — mapping is by label text only,
   never by class or position.) Closed months are immutable server-side; only the current
   month revises between visits.
2. **Completed-sales tables** — individual sale rows (`<tr id="{source}-{id}">`), grouped
   into up to 21 grade buckets. **Each bucket shows only the newest ~30 rows** — the site
   discards older ones forever. Sources: ebay, tcgplayer, goldin, heritage, pwcc.
3. **`VGPC.pop_data`** — the graded-population census: `{psa: [10 ints], cgc: [10 ints]}`,
   index *i* = grade *i+1*. A **current snapshot only** — the site keeps no census history.
4. **An image hash** — pointing at `images.pricecharting.com/{hash}/1600.jpg` on Google's
   CDN (325×450 despite the name).

### Data that does NOT exist at the source (cannot be scraped by anyone)

- **Historical sales volume.** No page, in any epoch we've captured, carries a
  volume-over-time series. The only "volume" on a detail page is a current-rate text label
  (`<tr class="sales_volume">`: "volume: 1 sale per day") — a snapshot, no time axis. The
  parser also hard-rejects unknown `chart_data` series as schema drift, so if the site ever
  adds a volume series, the crawl halts loudly the same day; it cannot be silently missed.
- **Sales older than the ~30-row bucket windows.** Once a row scrolls off, the site shows
  it to no one.
- **Population history.** Only the current census is published; history exists only from
  the moment *we* started observing.

Any spec that assumes deep volume history, complete deep sales history, or pre-2026 census
history is assuming data that does not exist. See §5 for what that means per table.

---

## 3. Entity reference

Provenance legend: **site** = parsed from pricecharting.com; **computed** = derived by the
worker; **user** = configuration the user edits; **db** = database-generated.

### 3.1 CardSet — table `sets`

A Pokemon card set (~303 exist) discovered on the category page. Enumeration bookkeeping
only — by explicit design decision, **no prices are ever taken from set pages** (their
price columns use the same class names as detail pages for different grades).

| Field | Type | Req | Source |
|---|---|---|---|
| `id` | long, PK identity | ✓ | db |
| `slug` | string(200), unique | ✓ | site — href after `/console/`, verbatim-encoded; the blacklist key |
| `name` | string(200) | ✓ | site |
| `discovered_at` | timestamptz | ✓ | computed — first enumeration |
| `last_seen_at` | timestamptz | ✓ | computed, mutable — last category-page listing |
| `last_walked_at` | timestamptz | opt | computed, mutable — last *completed* walk of its card listing; null = never (interrupted walks resume instead of waiting out the weekly interval) |

### 3.2 Card — table `cards`

One card (product). Identity + image bookkeeping (facts) sharing a row with the scheduler's
working memory (mutable, Rule 3). The card universe is **not a queue** — nothing is lined
up anywhere; each pick re-scores all candidates in Postgres and takes the single best.

| Field | Type | Req | Source |
|---|---|---|---|
| `id` | long, PK, **never generated locally** | ✓ | site — PriceCharting's product id (e.g. 630417); keeps every fact join consistent with the source |
| `set_id` | long, FK → sets | ✓ | computed — the set being walked at discovery. TODO: never updated if a card later moves sets (known small bug, back-burner) |
| `url` | string(500) | ✓ | site — detail path (`/game/pokemon-base-set/charizard-4`); parser rejects anything not site-relative under `/game/` (SSRF guard) |
| `name` | string(300) | ✓ | site |
| `image_hash` | string(64) | opt | site — CDN hash; doubles as the image's content address |
| `image_fetched_at` | timestamptz | opt | computed — null with hash set = image lane still owes a fetch |
| `first_seen_at`, `last_seen_at` | timestamptz | ✓ | computed — enumeration bookkeeping |
| `last_visited_at` | timestamptz, indexed | opt | computed, **mutable** — scheduler state |
| `observed_sales_per_day` | double | opt | computed, **mutable** — churn cache (Rule 3) |
| `any_bucket_at_cap` | bool | ✓ | computed, **mutable** — a bucket came back full with its oldest row newer than our previous visit: proof sales were missed; jumps the card to near the top of scheduling and raises the "Sales lost to a hot card" alert on the false→true flip |
| `failure_streak` | int | ✓ | computed, **mutable** — consecutive *card-attributable* failures (parse drift, 4xx-except-429); site-wide trouble (429/5xx) never counts |
| `quarantined_until` | timestamptz | opt | computed, **mutable** — retry-queue bench: 3 strikes → 1 day, doubling per repeat, 30-day cap; any success clears it |

### 3.3 CardPriceMonth — table `price_months`

**The deep history.** One observation of one (tier, month) cell of the price chart.
Change-only (Rule 2). A card's first visit backfills the site's entire chart — **six
tiers monthly back to ~Dec 2020** — so deep price history exists for every card from the
moment it's first visited. After that, a typical visit adds 0–2 rows (the current month
moved); closed months carry exactly one row forever.

| Field | Type | Req | Source |
|---|---|---|---|
| `card_id` | long, PK part, FK → cards | ✓ | visit context |
| `tier` | enum PK part: `Ungraded, Grade7, Grade8, Grade9, Grade9Half, Psa10` | ✓ | site — series key, label-verified (§2) |
| `month` | date, PK part | ✓ | site — epoch-ms chart point |
| `price_cents` | int | ✓ | site — monthly average price |
| `observed_at` | timestamptz, PK part | ✓ | computed — fetch time |

The composite PK ends in `observed_at`: the same (card, tier, month) legitimately has
multiple rows when the *current* month's average revised between visits. Latest-per-key
queries must order by `observed_at`.

### 3.4 CardPopulation — table `populations`

Graded-census history, one row per (grader, grade) cell **that changed**. Change-only
(Rule 2): a card's first visit stores its nonzero cells; later visits store only movements.
Deltas (grading activity) come from `LAG(population) OVER (PARTITION BY card_id, grader,
grade ORDER BY observed_at)`. History starts at *our* first observation — the site keeps
none (§2).

| Field | Type | Req | Source |
|---|---|---|---|
| `card_id` | long, PK part, FK → cards | ✓ | visit context |
| `grader` | string(8), PK part — only `psa` or `cgc`; any other key is schema drift | ✓ | site |
| `grade` | short 1–10, PK part | ✓ | site — array index + 1 |
| `population` | int | ✓ | site — count of graded copies |
| `observed_at` | timestamptz, PK part | ✓ | computed |

Census caveat: graders occasionally **restate** their counts (PSA restated ~June 2026;
one card's grade cell jumped 397 → 99,246). A >10× jump on an established base, or any
decrease, is flagged by metrics/alerts as a *source* change, not a market signal — but the
rows are still written. Analysis over population deltas should treat restatement-flagged
periods with suspicion.

### 3.5 Sale — table `sales`

**The sales ledger** — one immutable row per completed sale we have ever seen.
`UNIQUE (source, source_id)` is the dedup guarantee: re-scraping the same page re-offers
the same rows and the database ignores the duplicates (`ON CONFLICT DO NOTHING`), so the
ledger only ever grows with genuinely new sales. This table is the ground truth from which
churn and volume are *derived* (§6); nothing here is ever updated.

| Field | Type | Req | Source |
|---|---|---|---|
| `id` | long, PK identity | ✓ | db |
| `card_id` | long, FK → cards, indexed with `sold_on` | ✓ | visit context |
| `source` | string(16) | ✓ | site — row-id prefix: ebay, tcgplayer, goldin, heritage, pwcc |
| `source_id` | string(200) | ✓ | site — marketplace-native id, entity-decoded; stable across fetches |
| `sold_on` | date | ✓ | site |
| `grade_tier` | string(40) | ✓ | site — bucket label exactly as the page names it ("PSA 10", "Grade 9.5"); 21 distinct labels driven by the page's own selector |
| `price_cents` | int | ✓ | site — realized sale price |
| `listed_price_cents` | int | opt | site — original listing price when shown; most rows have none |
| `title` | string(500, clipped) | ✓ | site — **raw third-party text**, stored unencoded; whatever renders it must HTML-encode (XSS is the render layer's concern, by design) |
| `captured_at` | timestamptz | ✓ | computed — which visit ingested it (groups rows into visit batches) |

### 3.6 PageVisit — table `visits`

**The crawl's memory of where it has looked.** One row per fetch — distinguishes "we
looked and nothing changed" from "we never looked", which change-only fact storage alone
cannot. Also the raw feed for the rolling failure-rate spike alert and the layout tag
(`shape_hash`) on every failure.

| Field | Type | Req | Source |
|---|---|---|---|
| `id` | long, PK identity | ✓ | db |
| `kind` | enum: `CardDetail, Console, Category` | ✓ | computed. TODO: only `CardDetail` is ever written — Console/Category are speculative and candidates for deletion |
| `url` | string(500) | ✓ | computed |
| `card_id` | long, deliberately no FK | opt | visit context |
| `fetched_at` | timestamptz, indexed | ✓ | computed |
| `http_status` | int | ✓ | site response. TODO: the success path hardcodes 200 instead of the real status (known back-burner item) |
| `outcome` | enum: `Parsed, ParseFailed, HttpError` | ✓ | computed |
| `shape_hash` | string(64) | opt | computed fingerprint (§3.7) |

### 3.7 PageShape — table `shapes`

**The site-redesign tripwire.** Every fetched detail page is reduced to a structure-only
SHA-256 (layout, not values) and looked up here. Known hash → bump `last_seen_at`, move
on — the path virtually every visit takes. Never-seen hash → the site changed its markup:
the row is inserted, the raw HTML is archived once to `/var/lib/pokemon/shapes/{hash}.html`,
and a throttled alert fires. Rows exist for incident forensics — `visits.shape_hash` and
`parse_failures.shape_hash` tag every failure with the layout it happened under, and
`shape_json` is what you diff against the archive to fix the parser. A tiny, rarely-growing
table is the system working; it is *supposed* to look inert.

| Field | Type | Req | Source |
|---|---|---|---|
| `hash` | string(64), PK | ✓ | computed — structural SHA-256 |
| `shape_json` | jsonb | ✓ | computed — the structure that hashed |
| `sample_url` | string(500) | ✓ | computed — first page that produced it |
| `first_seen_at` / `last_seen_at` | timestamptz | ✓ | computed (`last_seen_at` mutable) |

This table is also why we can afford **no routine HTML archiving**: 100k cards × repeated
visits would be gigabytes of near-identical markup, so HTML is archived exactly once per
never-before-seen structure, and `shapes` is the dedup key that makes that possible.

### 3.8 ParseFailure — table `parse_failures`

**The refusal log.** When a page's structure or values violate the parsers' strict
expectations (schema drift), the crawl writes *nothing* to the fact tables and records here
instead: `url`, `fetched_at`, human-readable `reason`, and the `shape_hash` of the layout
that failed (all computed at refusal time). It's the "what exactly broke" side of a drift
incident — the shapes archive shows the new markup; this table shows which assertion it
tripped. Feeds the parse-failure-spike alert.

### 3.9 Non-database data

- **Images** — filesystem `{ImageDirectory}/{hash}/1600.jpg` (325×450), fetched once per
  hash from the Google CDN (different host, so outside the politeness budget), keyed back
  to cards via `image_hash`. ~3.6 GB at full corpus.
- **blacklist.json** (repo root) — **user input**: set slugs excluded from enumeration.
- **Scraper configuration** — **user input**: `appsettings*.json` → `ScraperOptions`
  (canary card list, burn-window safety fraction, page caps, directories). Not persisted
  per-entity; changing it changes future behavior, never recorded data.

### 3.10 Relationships

```
sets 1 ──< cards 1 ──< price_months     (FK, delete-restricted)
                 1 ──< populations       (FK, delete-restricted)
                 1 ──< sales             (FK, delete-restricted)
                 1 ──< visits            (card_id, deliberately no FK — visits outlive anything)
shapes ←─ referenced by hash from visits.shape_hash and parse_failures.shape_hash (no FK)
```

---

## 4. How data is written: the five lanes

Five independent background workers share one politeness gate (adaptive delay, 10 s floor /
300 s ceiling, against pricecharting.com only). Each detail-page visit is **one Postgres
transaction**: all fact rows, the visit row, and the card's scheduler-state update commit
together or not at all — a page is either fully ingested or left for retry.

| Lane | Cadence | Reads (third-party) | Writes |
|---|---|---|---|
| Detail crawl | continuous, one card at a time | `GET /game/{set}/{card}` | `price_months`, `populations`, `sales`, `visits`, `shapes`, `parse_failures`; card scheduler state |
| Enumeration | weekly (resumes interrupted walks immediately) | `GET /category/pokemon-cards`; `GET /console/{slug}` + cursor `POST` (site's own form fields, 150 cards/page) | `sets`, `cards` (identity fields) |
| Canary | every 6 h | detail pages of ~5 famous cards | nothing — hard assertions + alert only (early tripwire for site changes) |
| Images | hourly, 50/sweep | Google CDN `{hash}/1600.jpg` | filesystem + `cards.image_fetched_at` |
| Stats | every 5 min | counts over all tables | nothing — emits `crawl.*` gauges to New Relic |

**Scheduling (which card gets visited next):** pure priority score, re-computed from
Postgres each pick, highest tier wins — *due by burn window* (a selling card approaching
the point where its ~30-row bucket will start rolling sales off unseen; visited by 50% of
that window — the zero-missed-sales guarantee) → *never visited* → *bucket already at cap*
→ *starved past the 30-day floor* → everyone else by staleness × (1 + churn). Prevention
deliberately outranks discovery: a first-pass backlog must never make a known-hot card lose
sales. Quarantined cards are skipped until their bench date.

**The sale insert** (the one raw-SQL statement in the codebase; everything else is LINQ):

```sql
INSERT INTO sales (card_id, source, source_id, sold_on, grade_tier,
                   price_cents, listed_price_cents, title, captured_at)
SELECT ... FROM unnest(@sources, @sourceIds, @soldOns, @gradeTiers,
                       @priceCents, @listedPriceCents, @titles) AS u(...)
ON CONFLICT (source, source_id) DO NOTHING
```

All values arrive as typed array parameters — the SQL text never contains data, so hostile
listing titles are inert here (and must be encoded at render time, §3.5).

**Sample source payloads** (what the parsers consume):

```js
VGPC.chart_data = { "used": [[1606780800000, 24999], …], "cib": …, "new": …,
                    "graded": …, "boxonly": …, "manualonly": … }  // 6 tiers × ~68 months, cents
VGPC.pop_data   = { "psa": [0,0,1,4,12,40,120,300,900,99246], "cgc": [ …10 ints… ] }
```
```html
<tr id="ebay-256789012345">…<td>2026-07-12</td><td>PSA 10</td><td>$1,234.56</td>…</tr>
<tr class="sales_volume">… volume: <a>1 sale per day</a> …</tr>  <!-- current rate label; NOT captured, no history exists -->
```

---

## 5. How the data behaves over time — read this before designing any screen

Each history has a different **epoch structure**. Screens and analyses must not assume a
uniform "data starts at date X".

### Prices: deep and uniform

Backfilled to ~Dec 2020 for every card at its first visit. Monthly resolution, six tiers.
This is the only deep history we have, and it carries most of the undervaluation signal.
Multiple rows per (tier, month) occur only for the then-current month; take latest by
`observed_at`.

### Sales: two epochs with a ragged seam

- **Epoch boundary is per-card, per-grade-bucket** — *not* the crawler's start date. A
  card's first visit captures whatever its bucket windows still held: for a thinly-traded
  card that is real per-sale history reaching back months or years; for a hot card, days.
  "Start of reliable per-sale data" for a bucket = its oldest captured row.
- **Forward of first visit:** effectively complete. Completeness is *engineered, not
  guaranteed* — a card can outsell our visit pace and roll rows off unseen. That event is
  detected (`any_bucket_at_cap`), alerted ("Sales lost to a hot card"), and prevented by
  the burn-window scheduler tier; but a spec should say "complete except alarmed cap
  incidents", not "complete".
- **Monthly sales volume:** derivable from the ledger (§6) forward of each card's seam.
  **No pre-seam volume exists anywhere** — not in our store and not at the source (§2).
  A spec needing it must mark it *unavailable from source*, not "pending import".
- The frustrating asymmetry: per-sale history is shortest exactly where volume matters
  most (hot cards burn their windows in days).

### Population: forward-only, restatement-aware

History begins at each card's first visit (the site publishes no history). Deltas between
observations are grading activity — except during grader restatements (§3.4), which are
flagged. Change-only means most (card, grader, grade) cells have very few rows; that is
data, not sparsity.

### Operational history: from deploy

`visits`, `shapes`, `parse_failures` begin at first deployment (2026-07-28). `visits` is
the only place that proves a card was checked on a date when the fact tables show nothing
changed.

---

## 6. Derived views — compute these; do not look for stored columns

The store deliberately keeps raw facts and derives everything else. Canonical derivations:

**Latest price per tier (a card's "current prices"):**
```sql
SELECT DISTINCT ON (tier) tier, month, price_cents
FROM price_months WHERE card_id = :id
ORDER BY tier, month DESC, observed_at DESC;
```

**Churn (sales/day) as a time series** — the durable version of the
`cards.observed_sales_per_day` cache, and more accurate than it (the cache could only see
rows still on the page; the ledger keeps rows that had already scrolled off):
```sql
-- churn at any date d: sales in (d-30d, d] / 30.0
SELECT count(*) / 30.0 FROM sales
WHERE card_id = :id AND sold_on > :d::date - 30 AND sold_on <= :d;
```

**Monthly sales volume per card (valid forward of the card's seam, §5):**
```sql
SELECT date_trunc('month', sold_on) AS month, count(*) AS sales,
       avg(price_cents)::int AS avg_price_cents
FROM sales WHERE card_id = :id GROUP BY 1 ORDER BY 1;
```

**Grading activity (population deltas):**
```sql
SELECT grader, grade, observed_at,
       population - lag(population) OVER (PARTITION BY grader, grade ORDER BY observed_at) AS delta
FROM populations WHERE card_id = :id;
```

Also derivable, currently unused: discount-vs-list (`price_cents` vs `listed_price_cents`
where present), per-visit ingestion batches (`sales` grouped by `captured_at`), per-bucket
seam dates (`min(sold_on)` per `grade_tier`).

---

## 7. Who reads what today

- **The worker itself** — scheduler candidate queries over `cards`; last-known-value loads
  over `price_months`/`populations` for change-only planning; stats counts.
- **New Relic** — `crawl.*` metrics, logs, and alerts. Operational aggregates only; no NR
  widget ever displays a price, sale, or census value.
- **Nobody else.** The market data — the entire point of the system — has no consumer yet.
  The web app that will read `sales`, `price_months`, `populations`, and serve the images
  does not exist; its read API is undesigned. Until then every fact table is write-only
  from a product perspective.

---

## 8. Known gaps, quirks, and TODOs

- **TODO (design): web app read API** — undesigned; must HTML-encode `sales.title` at
  render (stored raw by design).
- **TODO (data): at-cap transition history** — the moments a card flipped into "sales were
  missed" are alerted+logged but not durably stored; only the current flag survives.
  Cheap fix if wanted: append a row (or a visit-row flag) on the false→true flip.
- **TODO (bug, back-burner): `cards.set_id` staleness** — never updated if a card moves
  sets after discovery.
- **TODO (cleanup): `visits.kind`** — Console/Category values never written; success rows
  hardcode `http_status` 200.
- **Unavailable from source, permanently:** historical sales volume; sales beyond the
  bucket windows; pre-observation census history (§2). If deep volume history ever becomes
  a hard requirement, the options are third-party (PriceCharting "Time Warp" paid feature —
  contents unverified; eBay/PSA/TCGPlayer archives — new integrations) or accepting the
  seam.
- **Slug encoding:** slugs are stored verbatim-encoded; build URLs from them untouched
  (double-encoding once caused 404s on every apostrophe set).
- **Restarts reset politeness:** the courtesy delay restarts at its 300 s ceiling on every
  deploy (~2.5 h re-ramp with slow-start) — visible as crawl-rate dips, not data loss.
