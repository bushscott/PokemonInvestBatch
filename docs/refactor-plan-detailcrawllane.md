# Refactor plan: splitting `DetailCrawlLane` (534 lines)

**Status: PROPOSAL — nothing implemented yet.**

This is the most load-bearing file in the system, so the plan is staged, and every stage leaves the
build green and the behaviour identical.

---

## What the file currently does

Nine distinct jobs live in one class:

| Lines | Job |
|---|---|
| 40–58 | The forever-loop and its error handling |
| 60–83 | Pause handling (the three-strike site cooldown) |
| 85–160 | Visit orchestration, tracing, and failure attribution |
| 162–181 | Writing a strike through a fresh DbContext when a visit died |
| 183–249 | Fetch → shape → parse → dispatch |
| 251–342 | **Persisting a parsed page** (history load, change-only planning, transaction, at-cap alert) |
| 344–370 | Recording a strike + quarantine alert |
| 372–402 | Population anomaly flagging + alert |
| 404–458 | **Choosing the next card** |
| 460–495 | Page fingerprinting, archiving, new-shape alert |
| 497–521 | Parse-failure-rate monitoring |

The two in bold are the ones that hurt: they are the largest, and they contain the only real
*decisions* in the file — which is exactly the code that currently has no unit tests.

---

## Stage 1 — extract the decisions (highest value)

These become pure classes in `Application/Scheduling`, matching every other decision in the
codebase, and get real unit tests in the existing `Application.Tests` project.

### 1a. `VisitSelection` — which card to visit next

Currently `PickNextCardAsync`, lines 404–458. The logic is:

1. If the bench-recheck slot is open and a benched card exists → retry it
2. Otherwise score the candidate pool and take the best
3. Compare that best score against the never-visited tier score
4. If never-visited wins → take an unvisited card instead

**This is the single most important extraction in the plan, because step 3–4 is where a critical
production bug already lived.** `PickNextCardAsync` once short-circuited to "unvisited first"
*before* scoring, which silently disabled the burn-window tier for as long as any unvisited
backlog existed — 46,997 cards at the time. It was found by watching a dashboard tile, not by a
test, because there was no test that could have caught it.

The extracted shape is pure and needs no database:

```
VisitSelection.Choose(benchSlotOpen, benched, candidates, now, options) -> Decision
    Decision = RetryBenched(cardId) | VisitScored(cardId) | VisitOldestUnvisited | Nothing
```

The lane keeps the queries and executes the decision.

**Tests this finally makes possible:**
- a burn-window-due card outranks the unvisited tier (the exact bug above)
- an open bench slot wins over everything
- a closed bench slot falls through to scoring
- an empty pool with unvisited cards still returns work
- a fully empty corpus returns `Nothing`

### 1b. `ParseFailureRate` — when a parse-failure spike is real

Currently `CheckFailureRateAsync`, lines 497–521. Pure rule: with at least 20 samples, alert when
the failure fraction exceeds the configured threshold.

```
ParseFailureRate.ShouldAlert(outcomes, threshold, minimumSamples) -> bool
```

**Tests:** below the sample floor never alerts even at 100% failure; exactly at threshold does not
alert; above does; an empty history is silent.

---

## Stage 2 — move the persistence bulk out of the Worker

Mechanical moves, no logic changes. Both land in `Infrastructure/Persistence` beside their
existing siblings `SaleWriter` and `ChangeOnlyPlanner`.

### 2a. `CardPageWriter` — persisting a parsed page

`WritePageAsync`, lines 251–342 (~91 lines). Loads prior history, plans change-only rows, opens
the transaction, appends, and updates the card's scheduler state.

Extracting also isolates a **known design flaw** worth naming rather than hiding: everything is
one transaction, so a failure on a cosmetic field (this actually happened — an over-length image
hash) discards the visit's already-parsed prices, populations, and sales. Once this code has its
own class, that flaw can be fixed in one place instead of surgery on the lane.

A small pure helper falls out of it and gets tests:

```
LastObserved.ByKey(rows, keySelector, timestampSelector, valueSelector)
```

— the "reduce history rows to the newest value per key" reduction on lines 260–271.

### 2b. `PageShapeArchive` — fingerprint, store, archive, alert

`RecordShapeAsync`, lines 460–495. Self-contained: hashes the page shape, upserts the row, writes
the sample HTML to disk on a first sighting, and alerts.

---

## Stage 3 — what stays

`DetailCrawlLane` keeps only what a lane should own: the loop, pause handling, the visit's tracing
and logging scope, dispatching on fetch/parse outcome, and failure attribution (the breaker, the
bench recheck, strike recording).

**Projected size: roughly 200 lines, down from 534.**

### Outcome (recorded after the work)

**Actual: 445 lines, down from 534 — a 17% cut, not the 60% projected.** The estimate was wrong
and it is worth saying why rather than quietly restating the goal.

What moved out was everything that was *not* the lane's job: the ranking (stage 1a), the spike
rule (1b), the shape archive (2b), and the write transaction (2a). What remains is ten methods,
each under about 110 lines, and every one of them is genuinely crawl orchestration — the loop, the
pause, the fetch/parse dispatch, failure attribution, the pick, and the two alarms.

Cutting further would mean inventing classes to hold things that only belong together because they
are the same size, which trades a large honest file for several small dishonest ones. The original
criticism was that this file was nearly double the next largest; at 445 against 302 it is no
longer an outlier, and the decisions it used to hide are now tested elsewhere. That was the point.

---

## Stage 4 — the Worker test project

After stages 1–2, the decisions are tested in `Application.Tests` and the persistence pieces are
reachable from `Integration.Tests`. What remains untested is the orchestration itself: *does a 404
actually produce a strike, and does a successful visit actually clear the quarantine?*

Two options, and they are not equivalent:

**Option A — `Worker.Tests` using the existing pattern.** A fake `HttpMessageHandler` (the
approach `PriceChartingClientTests` already uses) plus the live Pi database via `POKEMON_TEST_DB`.
Cheap to write. **Weakness: it skips in CI**, so the portfolio still shows skipped tests.

**Option B — `Worker.Tests` with Testcontainers.** Spins up a real PostgreSQL in Docker per test
run. Costs a NuGet dependency and roughly a minute of CI time.

**Recommendation: Option B.** It fixes a second problem at the same time — the 4 integration tests
that currently skip in CI would run for real. "Integration tests execute against a real database
in CI" is a substantially stronger signal than "integration tests exist but are skipped," and it
closes the gap honestly admitted in ADR-0003 about the imperative shell being under-tested.

---

## Risk and sequencing

| Stage | Risk | Why |
|---|---|---|
| 1a, 1b | **Low** | Pure extraction; the moved code is behaviour-identical and gains tests |
| 2a, 2b | **Medium** | Larger moves touching the write transaction — the one place data loss is possible |
| 3 | Low | Falls out of 1 and 2 |
| 4 | Low | Additive only |

Each stage is a separate commit, with the full suite green before the next. Stage 2 is the one to
deploy on its own and watch for a full crawl cycle before continuing, since it touches the
transaction that writes every price row.

**Recommended order:** 1a → 1b → 2b → 2a → 3 → 4. This front-loads the test coverage and puts the
riskiest change (2a, the write transaction) after the safety net exists.
