# ADR-0004: A broken page must not slow down the whole crawl

**Date:** 2026-08-06
**Status:** Accepted

## Context

This decision was forced by a production incident, and the incident is the clearest explanation of
the problem.

At 3:28 AM, the source site deleted a card's page. The system has two independent safety
mechanisms, and they combined into a trap:

1. **The politeness system** watches for trouble. Three failed requests in a row means "the site
   is struggling", so it pauses for 30 minutes and stretches the delay between requests to its
   5-minute ceiling. Every subsequent failure *doubles* the delay again; each success only shaves
   5 seconds off.

2. **The retry queue** re-checks benched cards roughly every 20 minutes, in case whatever broke
   has been fixed.

A card is not benched until its third failure, so the scheduler picked the dead page three times
in a row — instantly tripping the "site is in trouble" pause. From then on, every retry of that
one dead page slammed the delay back to the ceiling, faster than successful visits could claw it
back.

**The crawl fell from ~350 visits per hour to about 10, and stayed there for six hours.** One
deleted page had throttled the entire system by 35x. Nothing was wrong with the website at all.

## Decision

**Separate "this page is broken" from "this website is struggling."**

The quarantine system already made this distinction: a `3xx` or `4xx` response is the card's own
fault (a stale URL, a deleted page), while `429` and `5xx` are the site's. The politeness system
was not making that distinction — it treated every non-success as evidence of site trouble.

Now it does:

| Response | Meaning | Effect on the delay |
|---|---|---|
| `2xx` | Fine | Shrinks toward the floor |
| `429`, `503` | "Slow down" | Jumps straight to the ceiling |
| `5xx`, timeout, connection failure | Site is struggling | Doubles |
| `3xx`, `4xx` (except `429`) | **This page is broken** | **Nothing at all** |

A broken page also no longer counts toward the three-strikes site pause. Quarantine already owns
that problem, and one owner is enough.

## Alternatives considered

**Bench a card on its first failure instead of its third.** Would have prevented the triple-pick
that started the incident, but at the cost of benching cards for one-off network blips. The three
strikes exist for a good reason.

**Stop the retry queue from re-checking dead cards.** Treats the symptom. Retrying is genuinely
useful — a renamed card *does* come back after the next set walk.

**Make the delay recover faster.** Tuning numbers to survive a broken design. The delay was
behaving exactly as intended; it was being fed the wrong information.

## Consequences

**Good:**
- Proven within hours. That same evening, **24** dead pages hit the crawler at once. Throughput
  did not move: 353, 354, 351, 355 visits per hour straight through. Under the old behaviour this
  was a far worse version of the recipe that caused the outage.
- Politeness is unaffected where it matters. Real site distress — the case the mechanism exists
  for — still triggers the full response.

**Costs:**
- A page that returns `404` forever now costs one request per retry with no back-pressure from the
  politeness system at all. The quarantine's doubling sentence is the only brake, so that
  mechanism is now load-bearing on its own.
- If the site ever signals overload with a `4xx` status instead of `429` or `5xx`, this change
  would make the system ignore it. That is a deliberate bet on the site behaving conventionally.
