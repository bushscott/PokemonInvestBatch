# Glossary

Plain-English decoder for this project's jargon. The dashboard speaks human;
the code, metrics, and database keep their technical names — this file is the
bridge. (Being expanded as the dashboard title review continues.)

## Core vocabulary rules

These words mean exactly one thing everywhere on the dashboard:

- **Request** — one HTTP call to pricecharting.com, from any lane
  (card pages, set catalogs, spot checks). Widgets: "HTTP responses by
  status code", "Requests by lane", "Consecutive failed site requests".
- **Visit** — the whole errand for one card: fetch its detail page,
  parse it, write the rows, mark it checked. Only the detail lane makes
  visits. A visit *contains* one request. Widgets: "Card visit breakdown",
  "Visit duration", "Cards visited", "Slowest card visits", "New rows
  inserted per card visit".
- **Queue** — an explicit redo list the system maintains (currently only
  the retry queue for failing cards). The universe of scrapeable cards is
  NOT a queue: nothing is loaded or lined up in code — the scheduler asks
  Postgres to score all cards and hand over the single best one, each time.
- **Insert / create (rows)** — the database's point of view: rows are only
  ever added, never edited, never overwritten, never deleted ("append-only"
  in code comments; we avoid "append" in titles — it sounds like adding
  data *to* an existing row).

## Terms

| Jargon (code / metrics / alerts) | Plain English | Where it lives |
|---|---|---|
| corpus | all cards known to exist (~100k when discovery completes) | `crawl.corpus_size/_visited` metrics; "Card coverage" widget |
| canary | famous-card spot check: ~5 well-known cards re-fetched every 6h and hard-verified, as an early tripwire for site changes | `CanaryLane.cs`, `crawl.canary_failures`, "Famous-card spot checks failing" widget, NR alert "Canary failed" |
| quarantine | the retry queue: a card that failed 3 visits in a row *by its own fault* (unparseable page or 404-class error — never site-wide trouble) is set aside with a comeback date: 1 day, doubling per repeat, 30-day cap; one success clears it | `QuarantinePolicy.cs`, `cards.failure_streak/quarantined_until` columns, `crawl.cards_quarantined*` metrics, "Cards queued for retry" widgets |
| three-strike pause | after 3 consecutive failed requests the crawler assumes the *site* is in trouble and stops calling for 30 min | `AdaptiveDelay.ShouldPause`, `crawl.lane_paused` + `crawl.consecutive_failures`, "Consecutive failed site requests" widget |
| AIMD / politeness / courtesy delay | the gap between our requests: shrinks 5s per clean response toward a 10s floor, doubles on trouble toward a 300s ceiling, restarts at the ceiling on every deploy (~2.5h re-ramp) | `AdaptiveDelay.cs`, `crawl.delay_seconds`, "Politeness delay" tile + "Courtesy delay" chart |
| set walk / enumeration | cataloging: paging through a set's listing (150 cards/page) to learn what cards exist | `EnumerationLane.cs`, `sets.last_walked_at`, "Set discovery" widget |
| monotonicity violation | a higher grade selling cheaper than a lower grade on one card — normal market noise for thinly-traded cards; only a corpus-wide step change matters (it would mean the site silently remapped which chart series is which grade) | `GradeMonotonicity.cs`, `crawl.monotonicity_violations`, lab-shelf widget |
| pop anomaly / census restatement | an impossible change in PSA/CGC graded-population counts: a cell shrank, or jumped >10x on an established base — the grader changed how it counts, not the market (PSA did this June 2026: 397 → 99,246) | `PopulationRestatement.cs`, `crawl.pop_anomalies`, lab-shelf widget, NR alert "Population census restated" |
| sale-cap / bucket at cap | the site keeps only the last 30–50 sales per grade; a card is "at cap" when a visit proves sales scrolled off unseen since last time — it then gets fast-tracked revisits | `SalesObservation.cs`, `cards.any_bucket_at_cap`, `crawl.cards_at_cap`, "Sales lost to a hot card" alert |
| queue staleness | age in days of the longest-unchecked card — coverage freshness, alarmed at 35d | `crawl.queue_staleness_days`, "Longest-unchecked card" widget, NR alert "Scheduler starvation" |
| churn | a card's observed sales per day; higher churn = sooner revisit | `cards.observed_sales_per_day`, `VisitPriority.cs` |
| yield | rows created per card visit — how much each check is worth; sinks as the corpus matures, ~zero corpus-wide with green HTTP = site serving hollow pages | "New rows inserted per card visit" widget |
| shape / fingerprint | structure-only hash of a page (layout, not values); a never-seen hash means the site changed its markup — page HTML is archived and an alert fires | `PageFingerprint.cs`, `shapes` table, `/var/lib/pokemon/shapes/` |
| change-only append | the storage rule: a fact row is written only when its value differs from the last known; nothing is ever edited in place | `ChangeOnlyPlanner.cs`, composite PKs ending in `observed_at` |
| lane | one of five independent background workers sharing the politeness gate: detail crawl, enumeration, canary, images, stats | `Worker/Lanes/*.cs`, `lane` tag on `crawl.requests` |
| lane tag values | the `lane` tag on `crawl.requests` uses plain names: `card pages` (detail crawl), `set catalog` (enumeration), `spot check` (canary). Before 2026-07-29 the values were `detail` / `enumeration` / `canary` — queries over old windows see both | "Requests by purpose" pie slices |
| flex | New Relic's run-a-command-as-metrics mechanism; powers the pending-updates tiles | `ops/newrelic/apt-updates-*` |
| burn window / at risk | a selling card's burn window = days its sales rate takes to fill a ~30-row bucket, after which rows roll off forever; a card is "at risk" when that window is shorter than the revisit cycle — it survives only by fast-tracking. The scheduler guarantees a visit by half the window (prevention outranks even cap-hit revisits) | `VisitPriority.cs` burn-window tier, `crawl.cards_at_risk`, "Cards at risk of missing sales (predictive)" widget |
