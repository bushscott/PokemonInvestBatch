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
  parse it, write the rows, mark it checked. The detail lane makes visits
  on schedule; an express visit (intake API) is the same errand run on
  demand. A visit *contains* one request. Widgets: "Card visit breakdown",
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
| corpus | all cards known to exist (~91k across all 788 sets, once the last set walk lands) | `crawl.corpus_size/_visited` metrics; "Card coverage" widget |
| canary | famous-card spot check: ~5 well-known cards re-fetched every 6h and hard-verified, as an early tripwire for site changes | `CanaryLane.cs`, `crawl.canary_failures`, "Famous-card spot checks failing" widget, NR alert "Canary failed" |
| quarantine | the retry queue: a card that failed 3 visits in a row *by its own fault* (unparseable page or 404-class error — never site-wide trouble) is set aside with a comeback date: 1 day, doubling per repeat, 30-day cap; one success clears it | `QuarantinePolicy.cs`, `cards.failure_streak/quarantined_until` columns, `crawl.cards_quarantined*` metrics, "Cards queued for retry" widgets |
| delisted | retired by hand: the product left the site entirely (page gone *and* search finds nothing), so no set walk can ever heal its URL; the operator stamps `delisted_at` via SQL and the app skips the card everywhere — scheduling, retry queue, image sweep, neglect/at-risk alarms — while keeping every history row; only the operator un-delists, and the catalog can't signal when to — it also lists *phantom* products whose pages never existed (PriceCharting auto-generates a variant entry per card, so e.g. mirror-holo rows appear for secret rares that were never printed that way) | `cards.delisted_at` column, `crawl.cards_delisted` metric |
| retired / not a card | the parser proved the page was never a card — a handheld console, video game, or accessory the catalog filed under Pokemon (its chart speaks the game vocabulary Loose/CIB/New, so its prices would silently land under grade tiers); the verdict is the machine's and permanent, unlike delisting: the card leaves scheduling, the retry queue, and every alarm the moment it lands, its rows stay, and one alert names the whole set | `cards.not_a_card_at` column, `NotACardPageException`, `crawl.not_a_card` metric, "Pages retired (not cards), 24h" widget |
| intake API | the worker's one inbound surface: a localhost-only HTTP listener where sibling apps on this machine ask for a card refresh — queued (refresh request) or immediate (express visit); unreachable off-box by construction, so the bind address is the auth | `Worker/Intake/*.cs`, `Scraper:IntakePort` (default 5155), ADR-0006 |
| refresh request | the queued ask: "refresh this card soon" — stamped on the card and served at its own priority tier, the next slot unless a burn-window-due card owns it; cleared by the next successful visit from either path, standing through failures and quarantine | `cards.refresh_requested_at` column, `crawl.refresh_requests`, `crawl.refresh_wait_seconds`, `crawl.refresh_requests_pending` |
| express visit | the instantaneous ask: "refresh this card now and tell me when it's done" — the same errand as a lane visit run immediately, outside the scheduler *and* the polite gate, while the caller's HTTP request waits for the answer; one in flight at a time, a spacing floor between them, and the gate re-spaces around each one | `ExpressVisitRunner.cs`, `crawl.express_visits`, `crawl.express_visit_duration_seconds`, ADR-0006 |
| three-strike pause | after 3 consecutive failed requests the crawler assumes the *site* is in trouble and stops calling for 30 min | `AdaptiveDelay.ShouldPause`, `crawl.lane_paused` + `crawl.consecutive_failures`, "Consecutive failed site requests" widget |
| AIMD / politeness / courtesy delay | the gap between our requests: shrinks 5s per clean response toward a 10s floor, doubles on trouble toward a 300s ceiling, restarts at the ceiling on every deploy (~2.5h re-ramp) | `AdaptiveDelay.cs`, `crawl.delay_seconds`, "Politeness delay" tile + "Courtesy delay" chart |
| set walk / enumeration | cataloging: paging through a set's listing (150 cards/page) to learn what cards exist | `EnumerationLane.cs`, `sets.last_walked_at`, "Set discovery" widget |
| monotonicity violation | a higher grade selling cheaper than a lower grade on one card — normal market noise for thinly-traded cards; only a corpus-wide step change matters (it would mean the site silently remapped which chart series is which grade) | `GradeMonotonicity.cs`, `crawl.monotonicity_violations`, lab-shelf widget |
| pop anomaly / census restatement | an impossible change in PSA/CGC graded-population counts: a cell shrank, or jumped >10x on an established base — the grader changed how it counts, not the market (PSA did this June 2026: 397 → 99,246) | `PopulationRestatement.cs`, `crawl.pop_anomalies`, lab-shelf widget, NR alert "Population census restated" |
| sale-cap / bucket at cap | the site keeps only the last 30–50 sales per grade; a card is "at cap" when a visit proves sales scrolled off unseen since last time — it then gets fast-tracked revisits | `SalesObservation.cs`, `cards.any_bucket_at_cap`, `crawl.cards_at_cap`, "Sales lost to a hot card" alert |
| queue staleness | age in days of the longest-unchecked card — coverage freshness, alarmed at 35d | `crawl.queue_staleness_days`, "Longest-unchecked card" widget, NR alert "Scheduler starvation" |
| churn | a card's scheduling rate: its hottest grade bucket's fill rate in sales/day (recency-weighted, cap-corrected) — the fastest bucket rolls sales off first, so the card revisits on that bucket's clock; higher churn = sooner revisit | `cards.observed_sales_per_day`, `SalesObservation.cs`, `VisitPriority.cs` |
| yield | rows created per card visit — how much each check is worth; sinks as the corpus matures, ~zero corpus-wide with green HTTP = site serving hollow pages | "New rows inserted per card visit" widget |
| fingerprint | hash of the names a page uses, never their values; a never-seen hash is archived, HTML and all. Not a description of layout — it also captures how much data the card carries, so a quiet card fingerprints differently from a busy one | `PageFingerprint.cs`, `fingerprints` table, `/var/lib/pokemon/fingerprints/` |
| vocabulary | the names a fingerprint uses, qualified by bucket (`chart_data:psa10`); a name seen in no earlier fingerprint is the site changing its markup and the only thing that alerts — a novel hash alone is usually just a card with less data | `FingerprintVocabulary.cs`, alert "New page element observed" |
| change-only append | the storage rule: a fact row is written only when its value differs from the last known; nothing is ever edited in place | `ChangeOnlyPlanner.cs`, composite PKs ending in `observed_at` |
| lane | one of six independent background workers sharing the politeness gate: detail crawl, enumeration, canary, images, stats, delisted probe | `Worker/Lanes/*.cs`, `lane` tag on `crawl.requests` |
| lane tag values | the `lane` tag on `crawl.requests` uses plain names: `card pages` (detail crawl), `set catalog` (enumeration), `spot check` (canary), `express` (intake API express visits — not a lane, but a request source). Before 2026-07-29 the values were `detail` / `enumeration` / `canary` — queries over old windows see both | "Requests by purpose" pie slices |
| flex | New Relic's run-a-command-as-metrics mechanism; powers the pending-updates tiles | `ops/newrelic/apt-updates-*` |
| burn window / at risk | a selling card's burn window = days its sales rate takes to fill a ~30-row bucket, after which rows roll off forever; a card is "at risk" when that window is shorter than the revisit cycle — it survives only by fast-tracking. The scheduler guarantees a visit by half the window (prevention outranks even cap-hit revisits) | `VisitPriority.cs` burn-window tier, `crawl.cards_at_risk`, "Cards at risk of missing sales (predictive)" widget |
