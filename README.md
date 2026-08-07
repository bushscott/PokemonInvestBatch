# PokemonInvestBatch

A polite, self-healing web scraper that builds a long-term price history of the Pokémon card
market. It runs continuously on a Raspberry Pi and has collected **13.7 million rows** of price,
sales, and grading data across **72,000 cards** in **328 sets**.

The interesting part is not the scraping. It is everything built around the assumption that the
website *will* change, break, and lie — and that the system has to keep going anyway, unattended,
without ever hammering someone else's server.

---

## What problem this solves

Pokémon card prices move like a stock market, but there is no ticker tape. Prices are scattered
across a website that shows you *today* and only a little of yesterday. Sales roll off and are
gone.

This system watches that market continuously and writes down what it sees, permanently. Once a
sale is recorded it is never overwritten, so the history only grows. That archive is the actual
product — the scraper is just how it gets filled.

---

## The hard parts (and how they are handled)

### 1. Being a good citizen

Hammering a website is both rude and a fast way to get blocked. So every request in the entire
system passes through a single gate that enforces a waiting period between requests.

That waiting period is not fixed — it adapts. Clean responses shrink it gradually toward a
10-second floor. Any sign of trouble doubles it, up to a 5-minute ceiling. If the site says "slow
down" (HTTP 429), the delay jumps straight to the ceiling.

This is **AIMD** — Additive Increase, Multiplicative Decrease — the same algorithm that stops the
internet's traffic from collapsing. Gentle when things are fine, dramatic when they are not.

### 2. Never missing a sale

The website only shows the ~30 most recent sales per card. A card selling 6 times a day fills that
window in 5 days; after that, sales fall off the end and are lost forever.

So the scheduler does not visit cards in a simple rotation. It calculates, per card, how much of
its window has been consumed since the last visit, and fast-tracks any card at risk of losing
data. A dashboard tile counts cards past 75% of their window — if it is ever above zero,
scheduling is falling behind, and there is still a quarter-window of slack to fix it.

### 3. Surviving pages that break

A card page can break in ways that have nothing to do with the rest of the site: it gets renamed,
it gets deleted, or it becomes unparseable. If the scheduler kept picking a broken page, one dead
card could consume the entire politeness budget.

So a card that fails three times in a row on its own account is **quarantined** with a comeback
date — 1 day, doubling with each repeat, capped at 30 days. Any success clears it instantly.

Crucially, the system distinguishes *the card's fault* (a 404, a redirect) from *the site's fault*
(a 500, a timeout). Only site-wide trouble slows the crawl down. This distinction was learned the
hard way — see [ADR-0004](docs/adr/0004-card-faults-do-not-slow-the-crawl.md).

### 4. Noticing when the source is wrong

External data lies, and the system is built to notice rather than absorb it:

- **Canary checks** re-fetch a handful of famous cards every 6 hours with strict assertions. If
  the site's structure changes, this catches it within hours instead of at the end of a full pass.
- **Schema drift detection** refuses to guess. If a page's shape changes, parsing fails loudly and
  the card is quarantined — the system never writes data it is unsure about.
- **Impossible-value detection** flags census counts that shrink or multiply beyond any plausible
  pace. Graded cards do not become ungraded, so a shrinking count means the source restated its
  numbers — a bookkeeping event, not a market event, and downstream analysis must not confuse the
  two.

---

## Architecture

Four projects in a straight line. **Each one may only reference the one below it**, and this is
enforced by the compiler, not by good intentions:

```
   Worker            The moving parts: six independent background "lanes"
      |              plus all wiring and configuration
      v
 Infrastructure      Talking to the outside world: HTTP client, database,
      |              EF Core mappings and migrations
      v
  Application        Decisions: scheduling, politeness, quarantine, metrics
      |              (pure logic — no database, no network, no clock)
      v
    Domain           Understanding a page: HTML parsing and the types it
                     produces (zero dependencies on anything)
```

`Domain` has **no project references at all**. It is structurally impossible for a parser to reach
the database, because the compiler will not allow it.

### Functional core, imperative shell

The pattern that matters most here. Every real *decision* lives in a small class with **no
database, no network, and no clock**:

| Class | Decides |
|---|---|
| `VisitPriority` | which card to visit next |
| `AdaptiveDelay` | how long to wait between requests |
| `QuarantinePolicy` | when a card is benched, and for how long |
| `BenchRecheck` | when to retry a benched card |
| `SameCardFailureBreaker` | when one card is poisoning the crawl |
| `PopulationRestatement` | when a census change is impossible |
| `GradeMonotonicity` | when grade prices are inconsistent |

The lanes are the "shell" that does the messy real-world work of fetching and saving. Because the
decisions are pure, they can be tested by calling a function and checking the answer — no
databases, no fake web servers, and almost no mocks anywhere in the suite.

This is a deliberate trade against the more common "interface for everything" approach. The
reasoning is written down in [ADR-0003](docs/adr/0003-functional-core-over-ports-and-adapters.md).

### The six lanes

Independent background services, so one getting stuck cannot take down the others:

| Lane | Job |
|---|---|
| `EnumerationLane` | Walks set listings to discover which cards exist |
| `DetailCrawlLane` | Visits individual cards and records prices, sales, and grading counts |
| `CanaryLane` | Spot-checks famous cards every 6 hours to detect site changes fast |
| `ImageLane` | Downloads card images from the CDN |
| `StatsLane` | Publishes health metrics for the dashboard |
| `DelistedProbeLane` | Once a month, checks whether a retired card's page came back |

---

## How the data is stored

Three rules govern the database:

1. **Facts are never overwritten.** A recorded sale is permanent.
2. **History is change-only.** A price row is written only when the value differs from the last
   observation, so an unchanged month costs nothing and a changed one is preserved forever.
3. **Bookkeeping is separate from facts.** Scheduler state (last visit, failure counts) is
   mutable; observations are not.

Every table is defined in the EF Core model under
[`src/PokemonInvestBatch.Infrastructure/Persistence/`](src/PokemonInvestBatch.Infrastructure/Persistence/),
and those rules are asserted as tests against the compiled model — so a careless mapping change
breaks the build rather than the data. Project jargon is defined in [GLOSSARY.md](GLOSSARY.md).

---

## Operations

The system runs unattended, so it has to be able to report on itself.

- **Metrics and traces** are exported via OpenTelemetry to New Relic, where a dashboard shows
  crawl rate, courtesy delay, coverage, and queue health.
- **Alerts** fire on conditions that need a human: a canary failing, a card being quarantined, a
  card at risk of losing sales, a census restatement.
- **Structured JSON logs** go to `journalctl`, with every log line inside a card visit tagged with
  that card's URL, so one bad visit can be traced end to end.

---

## Running it

Requires [.NET 10](https://dotnet.microsoft.com/) and PostgreSQL 16+.

```bash
# 1. Create the database and roles
psql -f ops/postgres-setup.sql

# 2. Configure — copy the example and fill in your connection string
cp src/PokemonInvestBatch.Worker/appsettings.Production.example.json \
   src/PokemonInvestBatch.Worker/appsettings.Production.json

# 3. Apply migrations
dotnet ef database update -p src/PokemonInvestBatch.Infrastructure \
                          -s src/PokemonInvestBatch.Infrastructure

# 4. Run the tests (178 of them)
dotnet test

# 5. Run it
dotnet run --project src/PokemonInvestBatch.Worker
```

Set `Scraper:ContactEmail` before running. It goes into the User-Agent header on every request, so
the site owner can reach a human. Startup fails without it — deliberately.

Integration tests that need a live database are skipped unless `POKEMON_TEST_DB` is set.

### Deploying to a Raspberry Pi

```bash
./ops/publish.sh    # self-contained linux-arm64 build; no .NET runtime needed on the Pi
```

Then copy `publish/` to the Pi and restart the service. A systemd unit is in
[`ops/pokemon-invest-batch.service`](ops/pokemon-invest-batch.service).

---

## Design decisions

Significant decisions are recorded as ADRs in [`docs/adr/`](docs/adr/), each explaining what was
chosen, what was rejected, and what it costs.

| ADR | Decision |
|---|---|
| [0001](docs/adr/0001-append-only-history.md) | History is append-only and change-only |
| [0002](docs/adr/0002-manual-only-delisting.md) | Retiring a dead card is a human decision, never automatic |
| [0003](docs/adr/0003-functional-core-over-ports-and-adapters.md) | Pure decision classes instead of interfaces everywhere |
| [0004](docs/adr/0004-card-faults-do-not-slow-the-crawl.md) | A broken page must not slow the whole crawl |
| [0005](docs/adr/0005-pooled-grade-tiers.md) | Grading companies are pooled below grade 10 |

---

## Built with

C# / .NET 10 · PostgreSQL · Entity Framework Core · AngleSharp (HTML parsing) ·
OpenTelemetry · xUnit · Raspberry Pi

## A note on authorship

This project was built with heavy AI assistance (Claude Code), and the commit history says so
openly rather than hiding it. The architecture, the trade-offs, and every judgement call recorded
in the ADRs were mine — several of them made by overruling the assistant. I think that is the
honest way to present work done this way, and the ADRs are there so you can judge the reasoning
rather than take my word for it.
