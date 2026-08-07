# ADR-0002: Retiring a dead card is a human decision, never automatic

**Date:** 2026-08-03
**Status:** Accepted

## Context

A card called *Raikou [1st Edition]* started failing every visit. Its page had been deleted from
the source site: the URL redirected to a search page, and searching for the card returned nothing.
It was simply gone.

The system handled this as designed — three failures put the card in a retry queue, and the retry
queue kept checking it roughly every 20 minutes. But nothing could ever fix it, because there was
no page left to fix. It would have been retried forever.

There was an obvious automatic solution available. The system already knows which cards a set
listing contains. Any card that (a) is missing from the listing *and* (b) is currently failing
could be marked dead automatically.

## Decision

**A card is only ever retired by a human, with a SQL statement.** The application reads the
`delisted_at` column and honours it everywhere — scheduling, retries, image downloads, health
alarms — but the application will **never write that column itself**.

Bringing a card back is equally manual.

## Alternatives considered

**Auto-retire on two signals** (missing from the listing *and* failing). This was proposed and
rejected by the project owner. The reasoning: it makes a permanent judgement based on the correct
functioning of the parser. If the listing parser ever broke — and parsers do break, that is why
this codebase has drift detection everywhere — it could mass-retire hundreds of healthy cards in a
single run. The blast radius of a bug is unacceptable for an irreversible-looking action.

**Delete the row entirely.** Rejected: it would destroy the card's price history, violating
[ADR-0001](0001-append-only-history.md). A dead card's past sales are still real data.

**Do nothing and tolerate the noise.** Rejected once the cost became clear — a single dead card
generated ~100 warnings a day and permanently occupied the retry queue, which is itself supposed
to be an alarm signal.

## Consequences

**Good:**
- No bug can ever mass-retire the corpus. The worst case for a parser failure is noise, not data
  loss.
- The decision is auditable — a human made it, at a known time.
- History is fully preserved; a retired card keeps every sale it ever recorded.

**Costs:**
- Dead cards generate warnings until someone acts. This is accepted as a feature: it means the
  system *asks* rather than *decides*.
- Retired cards are invisible to the crawler, so the system loses its ability to notice if the
  page ever returns. This gap was later closed by
  [ADR-0004's](0004-card-faults-do-not-slow-the-crawl.md) sibling change — a monthly probe that
  re-checks retired cards and raises an alert if one answers again. Note that even that probe does
  not un-retire the card automatically; it tells a human, who decides.
