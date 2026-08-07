# ADR-0001: History is append-only and change-only

**Date:** 2026-07-27
**Status:** Accepted

## Context

This system exists to build a price history that gets more valuable the longer it runs. That only
works if old observations stay trustworthy.

The obvious design is to keep one row per card and update it as prices change. That is how most
"current price" apps work, and it is wrong here: the moment you overwrite yesterday's price, the
history is gone. You can never answer "what did this card cost last March?", and worse, you can
never *audit* your own data — if a scraper bug wrote a bad number, you cannot tell what the number
used to be.

There is a second problem. Prices are reported per card, per grade, per month. Writing every value
on every visit would mean re-recording years of unchanged history every single time, which is
millions of pointless rows.

## Decision

**Facts are written once and never modified or deleted.**

- A sale is permanent. Duplicates are prevented by a unique index on the marketplace's own ID, so
  re-reading the same page inserts nothing new.
- Price and grading history is **change-only**: a new row is written only when the value differs
  from the last recorded observation. Every row carries the timestamp it was observed.

Scheduler bookkeeping (when a card was last visited, how many times it has failed) is separate and
*is* allowed to change. That data describes our own process, not the world.

## Alternatives considered

**Update rows in place.** Simplest, and the default in most apps. Rejected because it destroys
history, which is the entire product.

**Write every value on every visit.** Keeps full history and is dead simple. Rejected on volume:
it would multiply the database by orders of magnitude to record that nothing happened.

**Store daily snapshots.** A common data-warehouse approach. Rejected for the same reason — it
records "no change" over and over, and the source does not update daily anyway.

## Consequences

**Good:**
- Any past state can be reconstructed exactly.
- A bad value can never silently replace a good one; both are visible with timestamps.
- When the source *restated* its own numbers (which happened — see the population anomaly
  detection), we could prove it, because we still had the old values.

**Costs:**
- Reading "the current price" means finding the newest row, not just selecting a column. This is
  slightly more work in every query.
- The database only ever grows. At 13.7 million rows this is comfortable; at a billion it would
  need partitioning.
- Correcting a genuine mistake means writing a newer row, not editing the old one.
