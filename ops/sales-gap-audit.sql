-- PokemonInvestBatch — find cards whose sale history has a hole in it.
-- Read-only. Writes nothing, locks nothing, safe to run against live prod:
--     ssh scott@<pi-ip> "cd /tmp && sudo -u postgres psql -d pokemon -f -" < ops/sales-gap-audit.sql
--
-- A GAP is a grade bucket whose page rolled past us between two visits: sales
-- happened, the bucket filled, and the oldest rows scrolled off before we
-- looked again. They are gone for good — the site shows only the newest rows
-- per bucket, and PriceCharting's paid API sells prices, never sale history.
--
-- Run this after any bug that could have starved the crawl. A card that has
-- been visited only once cannot have a gap (its single batch is the initial
-- backfill), so the eligible population grows as second visits land.
--
-- WHY THIS IS NOT SalesOverlap
-- The live code answers "did a bucket roll?" honestly, by counting the card's
-- rows either side of the write and asking whether the page and our records
-- shared anything (src/PokemonInvestBatch.Application/Scheduling/SalesOverlap.cs).
-- That needs the page. After the fact the page is gone, so this script
-- substitutes bucket FULLNESS for observed OVERLAP — the exact shortcut
-- SalesOverlap was written to avoid. It is sound here only because the graded
-- cap is invariant and the Ungraded estimate is conservative. Any report built
-- on this should say so.
--
-- THE BUCKET CAP IS NOT ONE NUMBER
-- Every graded bucket renders exactly 30 rows. The Ungraded bucket renders 30,
-- 50, or 60 depending on the page, so it is estimated per card as the largest
-- batch we ever received for it. Do NOT replace that with a constant: a
-- hardcoded 60 was built, approved, tested green, and reverted precisely
-- because it would halve revisit frequency for ~49k Ungraded-paced cards and
-- cause the very loss this script hunts for.
--
-- The estimate is floored at 30 because a bucket showing fewer than 30 rows
-- cannot have rolled — 30 is the smallest the site ever serves. Without the
-- floor the estimate is corrupted for any card ops/sales-gap-cut.sql has
-- already trimmed: the cut deletes the first-visit backfill batch, which is
-- the very batch that establishes the page size, and the estimate collapses to
-- whatever small batch survives. Observed live on card 11816189 after the
-- 2026-08-10 cut, where it fell from ~60 to 8.

\pset footer off

WITH batch AS (
    -- One row per visit that wrote sales. CardPageWriter stamps every row of a
    -- visit with the same `now`, so captured_at groups a page exactly; no
    -- rounding needed, and rounding would risk merging a lane visit with an
    -- express visit of the same card and hiding a full page between them.
    SELECT card_id,
           grade_tier,
           captured_at,
           count(*)     AS rows_written,
           min(sold_on) AS batch_oldest,
           max(sold_on) AS batch_newest
    FROM sales
    GROUP BY 1, 2, 3
),
sequenced AS (
    SELECT *,
           row_number() OVER w AS seq,
           -- The card's own page size for this bucket. For graded tiers the
           -- CASE below ignores it in favour of the invariant 30.
           max(rows_written) OVER (PARTITION BY card_id, grade_tier) AS est_page_cap,
           -- The newest sale we held going into this visit.
           max(batch_newest) OVER (PARTITION BY card_id, grade_tier ORDER BY captured_at
                                   ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) AS last_known_sale,
           lag(captured_at) OVER w AS prev_visit
    FROM batch
    WINDOW w AS (PARTITION BY card_id, grade_tier ORDER BY captured_at)
),
saturated AS (
    SELECT *,
           CASE WHEN grade_tier = 'Ungraded' THEN greatest(est_page_cap, 30) ELSE 30 END AS page_cap
    FROM sequenced
    WHERE seq > 1
)
SELECT s.card_id,
       c.name,
       t.slug                        AS set_slug,
       s.grade_tier,
       s.rows_written                AS new_rows,
       s.page_cap,
       s.last_known_sale             AS blind_from,
       s.batch_oldest                AS blind_to,
       round(EXTRACT(epoch FROM s.captured_at - s.prev_visit) / 86400.0, 2) AS visit_gap_days,
       -- Two independent proofs clear a batch. `new_rows < page_cap` means the
       -- page still showed rows we already held. Reach-back means the batch
       -- contains rows dated at or before our frontier — direct evidence the
       -- page reached back past our last visit, which happens because
       -- marketplaces report sales late. Either one means nothing was lost.
       CASE WHEN s.batch_oldest <= s.last_known_sale
            THEN 'reached back — likely no loss'
            ELSE 'GAP' END           AS verdict
FROM saturated s
JOIN cards c ON c.id = s.card_id
JOIN sets  t ON t.id = c.set_id
WHERE s.rows_written >= s.page_cap
ORDER BY verdict, s.card_id, s.captured_at;
