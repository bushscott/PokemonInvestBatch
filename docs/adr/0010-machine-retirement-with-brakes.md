# ADR-0010: The listing retires a card, the probe brings it back

**Date:** 2026-08-13
**Status:** Accepted (amends ADR-0002)

## Context

ADR-0002 made retirement a human verdict, for two stated reasons: an automatic
verdict would be **permanent** on the strength of **parser correctness**, and a
listing-parser break could therefore mass-retire hundreds of healthy cards in a
run. Both concerns were right and both cost us all the same: every card whose
product left the site (dupe cleanups like Moltres [Holo] #14 #14 13766134 and
Vaporeon [Reverse Holo] [Poke Ball] 13971735) cycled the quarantine bench
forever — three 302s, a sentence, a lapse, a retry, a re-bench, a Critical —
until an operator ran the same diagnosis by hand each time: *is the product id
still on its set's live listing?* Meanwhile renamed cards sat benched for up to
a week waiting for the scheduled walk to heal their URL, a blind spot ADR-0002's
own Raikou story describes.

The manual diagnosis was already mechanical. What was missing was an answer to
ADR-0002's two objections.

## Decision

**When a 3xx strike reaches the bench threshold, walk the card's own set
listing on demand and let the catalog testify.** One polite walk (the same
`SetWalker` the weekly enumeration uses — one implementation, two callers)
resolves the whole 302 family:

| the listing says | verdict |
|---|---|
| id present under a different URL | **renamed** — the walk healed the row; streak forgiven, no bench |
| id absent from a completed, non-empty walk | **removed** — `gone_at`, quietly |
| id present at the dead URL | **phantom** — benched exactly as before |

**`gone_at` is a machine verdict and a reversible one** — the third tombstone,
kept apart from `delisted_at` exactly as `not_a_card_at` is. The delisted probe
re-fetches gone cards on a self-doubling clock (first check a day after the
verdict, then the silence doubles: 1d, 2d, 4d… capped at 30) — the schedule
needs no counter column, because the gap between retirement and the last probe
*is* the counter. A 200 runs the **full visit errand**: the page is parsed,
written, and `gone_at` cleared in the same transaction. A false retirement
costs one day and un-does itself with fresh data.

**ADR-0002's objections, answered by construction:**

1. *Not permanent* — see above. The operator's `delisted_at` remains exclusively
   manual in both directions; the probe still only alerts on it.
2. *Not mass-retirable* — three independent brakes:
   - a verdict requires an existing card-attributable failure streak, so a
     listing-parser break alone retires nothing (nothing is checking);
   - an incomplete or zero-product walk renders **no verdict** — absence of
     evidence, not evidence of absence;
   - a circuit breaker: at ten machine retirements in a trailing day, further
     suspects bench as before and ONE Critical announces mass disappearance —
     a site event a human should see before the eleventh card goes.

**Machine verdicts do not email.** Gone, healed, and returned are log lines;
the only new Critical is the breaker. Alongside this, "Card quarantined" now
fires only when a card *joins* the bench (re-benching is bookkeeping — it used
to re-enter a single global 6h throttle window on every retry, which is why the
inbox read "failed 3 / 7 / 9 visits" from whichever card won each window).

## Consequences

- The bench returns to meaning "broken but present"; gone cards leave it the
  day they are diagnosed, and the operator's inbox carries human decisions only.
- Renames heal same-day instead of next-walk — the redirect blind spot closes
  without ever trusting a redirect target (it points at a search page).
- Sibling apps asking to refresh a gone card get 409 "gone" with a hint that an
  express visit can test the page now (express deliberately serves gone cards:
  a 200 un-retires by the same writer path).
- `crawl.cards_gone` sits beside `crawl.cards_delisted`; neither has a
  dashboard tile yet (manual import when wanted).

## Alternatives considered

**Alert-once quarantine and nothing else.** One line, kills the emails, keeps
gone cards cycling the bench forever, wasting recheck slots and leaving renames
to the weekly walk. Symptom relief.

**A hot-100-style fixed watchlist of failing cards for the operator.** Punts
the diagnosis back to the human, which is the loop this ADR exists to close.

**Auto-writing `delisted_at`.** Would erase the human/machine distinction that
made ADR-0002 trustworthy; a reversible machine column beside an absolute human
column keeps both meanings clean.
