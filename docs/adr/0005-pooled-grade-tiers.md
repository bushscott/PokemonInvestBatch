# ADR-0005: Grading companies are pooled below grade 10

**Date:** 2026-08-04
**Status:** Accepted

## Context

Trading cards get professionally graded on a 1–10 scale by several competing companies — PSA, CGC,
BGS, SGC, and others. A PSA 8 and a CGC 8 are not worth the same amount; PSA generally commands a
premium.

The source site reports sales in pooled buckets for grades 1 through 9.5 — a single "Grade 8"
figure covering every company — and only splits by company at grade 10 (PSA 10, CGC 10, BGS 10,
BGS 10 Black, and so on).

The individual sale records include the seller's listing title, which usually names the grading
company. So the company *could* be recovered by reading titles, and the tiers could be split
apart. The question was whether to do it.

## Decision

**Present grade tiers exactly as the source does: pooled for 1–9.5, split by company at 10.**

Do not attempt to derive per-company prices below grade 10, and do not apply an adjustment factor
to approximate them.

## The evidence

Mining 3.5 million sale titles produced three findings:

1. **PSA dominates the pool.** Of sales in the "Grade 8" bucket, 74.7% name PSA and 6.0% name CGC.
   Among sales where a company is identifiable at all, PSA is roughly **91%** of the volume.

2. **The pooled number is therefore already the PSA number.** CGC sells at roughly 0.68x PSA for
   the same card, but at 6% of volume it drags the blended average down by only about **2–3%**.

3. **Per-card samples are far too thin to split.** Fewer than **3%** of cards have as many as two
   sales from each of two different companies. A "CGC 8 price" for a specific card would usually
   be based on zero or one sale.

## Alternatives considered

**Split tiers by company using the listing titles.** The attribution itself is reliable — only
about 0.9% of graded sales name no company at all. But reliable labels on empty buckets are still
empty buckets. Rejected on finding 3.

**Apply a global multiplier** (e.g. "CGC ≈ 0.68x the pooled price"). Rejected by the project owner
as statistically dishonest: it projects a corpus-wide median onto individual cards where it may
not hold, and presents an estimate with the same confidence as an observation. Pooling is a known,
bounded inaccuracy; a fabricated multiplier is an unknown one.

**Show only PSA sales and discard the rest.** Rejected — it throws away real market data to make a
label technically precise.

## Consequences

**Good:**
- Displayed prices carry no invented precision. Every number shown traces to observed sales.
- The known error is small and quantified: pooling tracks PSA within about 2–3%.
- Nothing is lost permanently. Every sale keeps its raw title, so a company breakdown can be mined
  retroactively if the data ever gets thick enough to support it.

**Costs:**
- A card that trades unusually heavily in CGC will show a pooled price further from its PSA price
  than the corpus-wide 2–3%.
- Users who care specifically about a non-PSA company get no dedicated price below grade 10. The
  individual sales are still visible to them; only the summary is pooled.
- The interface must not imply the pooled figure is company-neutral. It is a PSA-dominated
  average, and calling it "market price" is only honest because PSA *is* the market here.
