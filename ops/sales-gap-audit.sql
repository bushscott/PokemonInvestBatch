-- PokemonInvestBatch — find cards whose sale history has a hole in it.
-- Writes nothing durable (one session-local temp view), locks nothing, safe
-- against live prod. Needs ops/sales-gap-common.sql BESIDE it (\ir), so:
--     scp ops/sales-gap-common.sql ops/sales-gap-audit.sql scott@<pi-ip>:/tmp/
--     ssh scott@<pi-ip> "cd /tmp && sudo -u postgres psql -d pokemon -f /tmp/sales-gap-audit.sql"
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
-- THE BUCKET CAP IS NOT ONE NUMBER — the rule, the per-card Ungraded estimate,
-- and its load-bearing floor of 30 live in ops/sales-gap-common.sql, shared
-- with the cut script so the report and the DELETE can never disagree.

\pset footer off

\ir sales-gap-common.sql

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
FROM sales_gap_candidates s
JOIN cards c ON c.id = s.card_id
JOIN sets  t ON t.id = c.set_id
WHERE s.rows_written >= s.page_cap
ORDER BY verdict, s.card_id, s.captured_at;
