# PokemonInvestBatch

[![CI](https://github.com/bushscott/PokemonInvestBatch/actions/workflows/ci.yml/badge.svg)](https://github.com/bushscott/PokemonInvestBatch/actions/workflows/ci.yml)

A polite, self-healing web scraper that builds a long-term price history of the Pokémon card
market. It runs continuously on a Raspberry Pi and has collected **13.9 million rows** of price,
sales, and grading data across **82,000 cards** in **788 sets**.

The interesting part is not the scraping. It is everything built around the assumption that the
website *will* change, break, and lie — and that the system has to keep going anyway, unattended,
without ever hammering someone else's server.

![Card coverage: 66,719 of 72,083 known cards visited; 351 cards in the last hour; 328 sets known,
none pending; a full lap of the corpus every 12.4 days](docs/images/dashboard-coverage.png)

---

## Why this exists, and how it was built

This is a portfolio project. I'm looking for my next role, and I wanted something whose hard parts
were genuinely hard rather than tutorial-hard: a live system running unattended, against a data
source I don't control and can't fix when it breaks.

I also built it as a deliberate way to learn agentic AI development, which increasingly looks like
the job rather than a novelty alongside it. Most of the code here was written by Claude Code
working from my direction, and nearly every commit says so in its trailer. Hiding that would be
dishonest, and it would also miss the point.

**My value as a senior developer was never typing speed.** It's scar tissue — the accumulated
memory of how things actually fail. It's the instinct that says *this will be fine right up until
the day it isn't*, along with the specific reason why, because I watched it happen once and never
forgot. Nobody pays a senior engineer to write the loop. They pay for the pause before the loop
ships.

That skill did not become less valuable when a machine started writing the code. It became the
only part that mattered.

Prompt engineering is not the hard part of AI-assisted development. Prompts are cheap, and if one
doesn't work you write another. The hard part is reading code that is fluent, confident,
well-commented, internally consistent — and quietly wrong — and knowing which one is in front of
you. AI produces *plausible*. Plausible is not correct, and the distance between them is exactly
where experience lives.

Three places in this repo where that distance mattered:

- **[ADR-0002](docs/adr/0002-manual-only-delisting.md)** — a proposed feature would have
  automatically retired dead cards. The code was correct. I killed it anyway, because it made a
  permanent decision conditional on a parser staying correct, and parsers break. That is a shape
  I have seen before: the automation works perfectly until the day its input is wrong, and then it
  is wrong at scale, quietly, across thousands of rows. Nothing in the diff was flawed. The
  problem was the blast radius, and blast radius is not visible in a diff.
- **[ADR-0005](docs/adr/0005-pooled-grade-tiers.md)** — a suggested "CGC ≈ 0.68x PSA" price
  adjustment, backed by a real corpus-wide figure. I turned it down. The number was true in
  aggregate and would have been fiction per card, and I have watched estimates get laundered into
  facts by nothing more than being displayed next to real ones.
- **[ADR-0004](docs/adr/0004-card-faults-do-not-slow-the-crawl.md)** — the one I had no scar for,
  and therefore missed. Generated code treated a single broken page as evidence the whole website
  was struggling. It reviewed clean, because two individually sensible safety mechanisms only
  combine into a trap under conditions neither of them can see. Production found it instead: one
  deleted card page throttled the crawler from 350 requests an hour to about 10 and held it there
  for six hours. I have the scar now.

I also learned to distrust a confident summary. At one point I was told a card set was probably
finished growing — reasoning drawn from my own database's discovery dates, which only recorded
when my scraper first ran and proved nothing whatsoever about the set. Checking an independent
source answered the question properly and reversed the conclusion.

None of that is an argument for or against building this way. It is what the work actually looks
like: the machine writes fast, and the judgement about what to keep is still the job. The ADRs in
[`docs/adr/`](docs/adr/) exist so you can assess that judgement directly rather than take my word
for it.

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
- **Schema drift detection** refuses to guess. If a page's structure changes, parsing fails loudly and
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

### The map

Nine tables, in three groups: a **catalog** of what exists, an **append-only record** of what the
site said, and a **diary** of what the crawler did.

[![Schema map: catalog (sets, cards), append-only record (price_months, populations, sales),
and diary (visits, fingerprints, parse_failures)](docs/images/data-model.svg)](docs/images/data-model.svg)

**The catalog** — `sets` and `cards` — is the only mutable part. A card's row carries both its
identity (url, name) and the scheduler's working notes: when it was last read, how fast it sells,
whether it is benched. These change constantly, and that is fine, because nothing here is a fact
about the market.

**The record** — `price_months`, `populations`, `sales` — is never edited. The first two end their
primary key in `observed_at`, which is what makes them append-only by construction rather than by
convention: writing the same value twice is impossible to do wrongly, because a new observation is
simply a new row. And they are *change-only* — a price is written only when it differs from the
last one seen, so a quiet month costs nothing while a moving one is preserved forever. `sales` is
simpler: a sale is a discrete event with the site's own listing id, so it is written once and never
touched again.

**The diary** — `visits`, `fingerprints`, `parse_failures` — records what happened rather than what is
true. `visits` holds one row per fetch, successful or not, which is what makes it possible to ask
"how often did we actually look at this card?" as opposed to "when did we last succeed?".
`fingerprints` is the site's changelog, kept by us because the site does not publish one.

Deliberately, `visits` has **no** foreign key to `cards` while the three record tables do, with
`ON DELETE RESTRICT`. The diary should survive its subject; the facts should be impossible to
orphan.

As of August 2026 that is 9.55M price rows, 4.02M sales and 355k population cells across 82,000
cards in 788 sets — about 13.9M observations, none of which has ever been updated in place.

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

![Politeness delay at its 10-second floor; zero consecutive failed site requests; zero famous-card
spot-check failures; zero percent parse failures](docs/images/dashboard-health.png)

Every tile is phrased as a promise the system makes, so a glance is enough: the courtesy delay
sits at its 10-second floor rather than backed off, nothing has failed in a row, and the parser
still understands the site.

![Three charts: average milliseconds per visit phase, HTTP responses by status code over 24 hours,
and visit duration p50 versus p95](docs/images/dashboard-timings.png)

The middle chart is the one worth reading closely. Volume collapses at about 2am and does not
recover until nearly 10am — that is a real outage, caught here rather than by a user. A single
deleted card page had convinced the politeness controller that the whole site was struggling, and
the crawl throttled itself from roughly 350 requests an hour to about 10 for six hours. The
diagnosis, the fix, and what it cost are written up in
[ADR-0004](docs/adr/0004-card-faults-do-not-slow-the-crawl.md); the recovery is the vertical wall
on the right-hand side of the same chart.

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

# 4. Run the tests (219 of them)
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

## Licence — please read, please don't run

All rights reserved. The source is here to be read and reviewed; it is not
licensed for use. See [LICENSE](LICENSE).

That is deliberate, and it is not about protecting the code. Everything above
about politeness — the single shared gate, the delay that backs off at the
first sign of strain, the contact address in every User-Agent — only works
while exactly one copy is running. A hundred individually well-behaved clients
arriving at the same time is not good manners; it is an outage, and the people
who would deal with it never asked to be scraped in the first place.

So take the ideas freely. Just don't point another copy at their servers.
