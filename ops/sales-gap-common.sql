-- PokemonInvestBatch — the ONE spelling of the sales-gap rule, shared by
-- ops/sales-gap-audit.sql (read-only report) and ops/sales-gap-cut.sql (the
-- DELETE). Included by both via \ir, which resolves relative to the including
-- file — so keep the three files in one directory, ops/ locally and /tmp on
-- the Pi. Before this file existed the rule lived in each script by
-- copy-paste, with the load-bearing bucket-cap floor hardcoded twice; drift
-- between an audit and a script that deletes sale rows is not a risk worth
-- keeping for the convenience of one fewer scp.
--
-- Creates a TEMP VIEW (session-local, writes nothing durable):
--
--   sales_gap_candidates — one row per (card, grade bucket, revisit batch),
--   first-visit backfills excluded (a card's first batch cannot be a gap;
--   truncation there is inherent, not a miss). Columns beyond the batch
--   basics:
--     page_cap        — what a FULL page means for this bucket. Graded tiers:
--                       the invariant 30. Ungraded: the largest batch this
--                       card ever returned, floored at 30. The floor is
--                       load-bearing: 30 is the smallest page the site serves,
--                       so a smaller bucket cannot have rolled — and without
--                       it, any card the cut script already trimmed reports a
--                       collapsed cap (the cut deletes the first-visit batch
--                       that established the page size; observed live on card
--                       11816189, ~60 -> 8). NEVER a constant 60: that was
--                       built, approved, tested green, and reverted, because
--                       it would halve revisit frequency for ~49k cards.
--     last_known_sale — our frontier going into the visit. A batch whose
--                       oldest row is at or before it REACHED BACK past the
--                       frontier: direct proof nothing scrolled off unseen
--                       (marketplaces report sales late, so pages do this).
--     prev_visit      — the visit before, for gap-width reporting.
--
--   A batch is gap-shaped iff rows_written >= page_cap (nothing we held was
--   still on the page) AND batch_oldest > last_known_sale (no reach-back).
--   Each script applies those two lines itself, visibly, so a reader of
--   either sees the whole rule.

CREATE TEMP VIEW sales_gap_candidates AS
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
           max(rows_written) OVER (PARTITION BY card_id, grade_tier) AS est_page_cap,
           max(batch_newest) OVER (PARTITION BY card_id, grade_tier ORDER BY captured_at
                                   ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING) AS last_known_sale,
           lag(captured_at) OVER w AS prev_visit
    FROM batch
    WINDOW w AS (PARTITION BY card_id, grade_tier ORDER BY captured_at)
)
SELECT card_id,
       grade_tier,
       captured_at,
       rows_written,
       batch_oldest,
       batch_newest,
       last_known_sale,
       prev_visit,
       CASE WHEN grade_tier = 'Ungraded' THEN greatest(est_page_cap, 30) ELSE 30 END AS page_cap
FROM sequenced
WHERE seq > 1;
