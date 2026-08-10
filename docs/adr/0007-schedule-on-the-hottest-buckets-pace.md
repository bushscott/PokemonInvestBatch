# ADR-0007: The schedule follows the hottest bucket, and a capped card warns its set

**Date:** 2026-08-10
**Status:** Accepted

## Context

This decision was forced by a production incident — the exact class of loss the scheduler was
built to prevent.

On Aug 9–10, two cards in the same set (pokemon-ascended-heroes) permanently lost sales: the
site keeps only the newest ~30 sales *per grade*, and one grade's list turned over completely
between visits. The uncomfortable part, confirmed from the visit ledger: **the scheduler visited
both cards almost exactly when it planned to.** The 3.46-day gap was precisely the fast-track
point its math asked for. Capacity was not the problem. The plan was.

The plan was built on one number: `sales in the trailing 30 days ÷ 30`, across all grades. That
formula has three blind spots, and the incident hit all three at once:

1. **A trailing average dilutes a burst.** Psyduck #226 was visibly selling 11–14/day for the
   five days before its last safe visit — the burst was *on the page we parsed* — but a sleepy
   July dragged the average to 4.4/day. The schedule trusted a number that was three times stale.
2. **The page censors its own evidence.** The visible history caps at 30 rows per grade, yet the
   formula divided by a fixed 30 days. The hotter the card, the shorter the window those 30 rows
   actually cover — and the more the formula *under*-counts exactly the cards it must not.
3. **The cap is per grade; the model was per card.** A burst concentrated in one grade (here,
   PSA 10) fills that grade's 30-row bucket long before the card-wide average looks dangerous.

A capacity check before deciding anything: corpus-wide churn sums to ~25k sales/day, so visiting
every selling card fast enough to never lose a row costs ~840 visits/day of an ~8,400/day polite
budget. Honesty was affordable — roughly 5× headroom. The estimator was the only thing broken.

## Decision

Three parts, shipped together.

**1. The scheduling rate is the hottest grade bucket's fill rate.** Per grade, the estimator
takes the better of the steady rate (`rows ÷ 30`) and the fastest credible recent prefix: the
*k* newest rows spanning *d* days sold at *k/d*, maximised over *k* ≥ 3. The card's rate is the
maximum across its buckets. One expression buys both missing corrections — the full-bucket
prefix *is* the cap-corrected span rate, and the short prefixes *are* the recency weighting.
Three rows is the credibility floor (one or two same-day rows are indistinguishable from a
single collector), and one day is the smallest window a date-only source can honestly express.

`cards.observed_sales_per_day` now *means* this number. No schema change, and deliberately no
second column: the burn-window inequality (`staleness × rate ≥ fraction × 30-row bucket`) runs
server-side against this column in the scheduler's SQL, and it is only per-grade-correct when
the stored rate is the hottest bucket's. Card-wide churn remains exactly derivable from the
sales ledger, which was already the more accurate source (DATA_MODEL §6).

**2. A capped card fast-tracks its set's sellers.** Hype is set-shaped — both losses were the
same set, days apart. When a visit proves a bucket rolled over (the false→true at-cap edge), the
crawl stamps a refresh ask on that set's 25 hottest known sellers, reusing the intake API's
machinery (ADR-0006): same column, same priority tier — behind burn-window prevention, ahead of
everything else — same clearing rule. The siblings are then served by the normal polite crawl,
one page per courtesy-delay slot. **Never by express visits:** contagion reorders the queue; it
must not add traffic. Twenty-five is half the ask tier's serve window, so one hyped set cannot
crowd a real caller's ask out of the queue.

**3. The ground truth is loud again.** The `crawl.cards_at_cap` gauge — cards with proof that
sales rolled off unseen — got its dashboard tile back ("Cards selling faster than we can
track"), sitting beside the predictive at-risk tile so the pair reads prediction | fact. The
count now excludes retired cards, whose sticky at-cap flag would otherwise pin the tile red
forever.

## Alternatives considered

**A shorter trailing window (7-day average).** Less stale, still an average: a two-day burst is
still diluted, and it is still per-card while the cap is per-grade. Fixes the symptom's size,
not its shape.

**A safety multiplier on the old rate.** Scheduling on `2×` the stale number punishes every calm
card with extra visits while still trusting the stale number's *shape* — a 3× stale reading
survives a 2× margin.

**A second column for the scheduling rate.** Keeps the old column's meaning pure, but the
scheduler's SQL reads one column, and two cached rates invite them to disagree. The card-wide
number already lives, more accurately, in the sales ledger.

**Contagion via express visits.** Immediate, and rejected outright: express bypasses the polite
gate, so a hyped set would translate directly into a burst of extra traffic against the site.
The queue reorder gets the same cards seen within the hour at zero added load.

**Backfilling missed sales from the source.** The site's paid API sells current prices, not sale
history (checked). The true upstream — marketplace sold-listings with a ~90-day lookback — is a
real but separate project, and it recovers losses instead of preventing them. Deferred, not
rejected.

## Consequences

**Good:**
- Proven the same hour it deployed. The incident cards re-rated 4.4 → 22/day and 5.4 → 30/day
  and moved to sub-daily revisit clocks; a base-set Mewtwo was caught mid-burst (+42 sales,
  re-rated to 7/day) with nothing lost. The incident replay is a permanent unit test: the old
  formula planned an 11-day revisit on Psyduck's real page; the new one plans 1.07 days.
- A closed-loop simulation (scripted bursts against a 30-row-per-grade page render) pins the
  class: no synthetic sale may ever roll off unseen, and post-burst revisit intervals must only
  lengthen. No oscillation is possible by construction — the estimator is stateless and monotone
  in the page.

**Costs:**
- Deliberate over-estimation of steady sellers, up to ~1.5–3× at small prefix sizes. A 1/day
  card reads ~1.5/day and gets visited every ~10 days instead of 15. This is the safe side of
  the trade by design; the ~5× capacity headroom absorbs it, and `MinBurstRows` is the single
  knob if visit volume ever needs calming.
- `observed_sales_per_day` no longer means "card-wide sales per day." Anything downstream
  wanting that number must derive it from the sales ledger — which was always the honest source.
- A cold card that goes from nothing to a full bucket in one grade *between* two visits can
  still lose that bucket's tail, once. The estimator cannot see a burst no visit has witnessed;
  contagion (a set-mate usually caps first) and the restored tile bound and expose the case
  rather than eliminate it.
