# ADR-0003: Pure decision classes instead of interfaces everywhere

**Date:** 2026-07-27
**Status:** Accepted

## Context

The standard way to make .NET code testable is to hide every external dependency behind an
interface — `IPriceChartingClient`, `ICardRepository` — and inject it. Tests then supply fake
implementations (mocks) instead of the real thing. This is Ports & Adapters, also called Hexagonal
Architecture, and in enterprise .NET it is close to universal.

It has a real cost. Every interface is indirection: a reader following the code has to jump from
the interface to find the one class that implements it. And mock-based tests have a well-known
failure mode — they verify that your code called the mock the way you told the mock to expect,
which can pass while the real system is broken.

The question is what the interfaces are actually *for* here. There is one website and one
database. There is no second implementation, and no plausible future one.

## Decision

**Extract the decisions, not the dependencies.**

Every genuine decision this system makes lives in a small class with no database, no network, and
no clock. It takes plain values in and returns a plain answer:

- `VisitPriority` — which card deserves attention next
- `AdaptiveDelay` — how long to wait between requests
- `QuarantinePolicy` — whether a card should be benched, and until when
- `BenchRecheck` — whether it is time to retry a benched card
- `SameCardFailureBreaker` — whether one card is poisoning the crawl
- `PopulationRestatement` — whether a change in grading counts is physically possible
- `GradeMonotonicity` — whether a set of grade prices is internally consistent

The "lanes" are a thin shell around these that does the fetching and saving. This split is known
as **functional core, imperative shell** (Gary Bernhardt, 2012).

The codebase has exactly one interface: `IAlerter`, which exists because there genuinely are two
plausible implementations (log-based and email-based).

## Alternatives considered

**Full Ports & Adapters.** Rejected as speculative generality — an interface with exactly one
implementation, forever, is indirection with no payoff. Mark Seemann, who wrote the standard .NET
dependency-injection book, makes this argument directly: an abstraction that is never reused is a
smell, not a virtue.

**No separation at all** (decisions inline in the lanes). Rejected: it is what makes most scrapers
untestable, because you cannot exercise "what should happen after 3 failures?" without a live
website.

## Consequences

**Good:**
- 178 tests with almost no mocking. Testing the quarantine rule means calling a function with a
  failure count and checking the date it returns — no fakes, no setup, no fixtures.
- The tests assert *behaviour* ("the 4th failure benches the card for 2 days"), not interactions
  ("the repository's Save method was called once"). They keep working when internals are
  refactored.
- Reading the code is a straight line. There is no interface to chase.

**Costs:**
- **This will be questioned in code review**, because it does not match the dominant enterprise
  idiom, and the layer names (Domain / Application / Infrastructure) lead a reader to expect
  Ports & Adapters. That expectation is the reason this ADR exists.
- The lanes themselves — the imperative shell — are harder to unit test, because they hold
  concrete dependencies. They are currently covered by integration tests against a real database
  rather than unit tests. This is a genuine gap, not a claimed virtue.
- If a second data source is ever added, interfaces will have to be introduced at that point.
  That is deliberate: the abstraction gets designed when there is a real second case to design
  against.
