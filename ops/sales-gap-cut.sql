-- PokemonInvestBatch — make each gapped card's sale history continuous again.
--
-- DESTRUCTIVE AND IRREVERSIBLE except through the CSV this writes first.
-- Run as pokemon_owner or the postgres superuser: pokemon_app has no DELETE
-- grant on any table (ops/postgres-setup.sql) and that restriction stays.
--
-- Needs ops/sales-gap-common.sql BESIDE it (\ir cannot resolve from stdin):
--     scp ops/sales-gap-common.sql ops/sales-gap-cut.sql scott@<pi-ip>:/tmp/
--     ssh scott@<pi-ip> "cd /tmp && sudo -u postgres psql -d pokemon -v ON_ERROR_STOP=1 -f /tmp/sales-gap-cut.sql"
--
-- Run ops/sales-gap-audit.sql first and read its output. This script derives
-- its own cut list from the same shared view (ops/sales-gap-common.sql), so
-- the two cannot drift apart — but that also means it will act on whatever
-- the audit currently finds, including gaps discovered since you last looked.
--
-- WHAT IT DELETES
-- For each card the audit calls a GAP, every sale row that sold before the
-- card's LATEST gap. Cutting at an earlier gap would leave a later hole inside
-- the surviving rows and defeat the point: Snorlax #76 had two gaps, and
-- cutting at the first would have left 54 rows stranded on the far side of the
-- second.
--
-- WHAT IT LEAVES ALONE
--   * Cards cleared by reach-back — their page listed sales at or before our
--     frontier, so nothing scrolled off. They are not gaps.
--   * price_months and populations. Those are change-only writes: a missing
--     row means "checked, unchanged", and the site's chart restates full
--     monthly history on every visit, so they self-heal and were never
--     discontinuous. Do not "repair" them.
--   * cards.observed_sales_per_day. It is a cache recomputed from the live
--     page on the card's next visit, not from stored history.

\set ON_ERROR_STOP on
\timing off

BEGIN;

-- The cut list, derived from the SAME rule the audit prints — literally: both
-- read the sales_gap_candidates view from ops/sales-gap-common.sql, which owns
-- the per-tier bucket cap and its load-bearing floor of 30. The two used to
-- share the rule by copy-paste, which made "can never drift apart" a hope
-- rather than a property; in a script that deletes sale rows, hope is not a
-- mechanism.
\ir sales-gap-common.sql

CREATE TEMP TABLE cut_list AS
WITH gaps AS (
    SELECT *
    FROM sales_gap_candidates
    WHERE rows_written >= page_cap                -- nothing we held survived
      AND batch_oldest > last_known_sale          -- reach-back clears the batch
)
SELECT DISTINCT ON (card_id)
       card_id,
       batch_oldest AS cut_at                     -- first sale after the latest gap
FROM gaps
ORDER BY card_id, captured_at DESC;

\echo ''
\echo '=== Cut list ==='
SELECT k.card_id, c.name, k.cut_at,
       (SELECT count(*) FROM sales s WHERE s.card_id = k.card_id AND s.sold_on <  k.cut_at) AS to_delete,
       (SELECT count(*) FROM sales s WHERE s.card_id = k.card_id AND s.sold_on >= k.cut_at) AS to_keep
FROM cut_list k JOIN cards c ON c.id = k.card_id
ORDER BY k.card_id;

-- The undo. Written before anything is destroyed, as a file rather than a
-- table so the schema stays the product's data model and not a record of what
-- we did to it.
\echo ''
\echo '=== Writing rollback CSV to /tmp/sales-cut.csv ==='
\copy (SELECT s.* FROM sales s JOIN cut_list k ON k.card_id = s.card_id WHERE s.sold_on < k.cut_at ORDER BY s.card_id, s.sold_on) TO '/tmp/sales-cut.csv' CSV HEADER

DO $$
DECLARE
    expected bigint;
    removed  bigint;
BEGIN
    SELECT count(*) INTO expected
    FROM sales s JOIN cut_list k ON k.card_id = s.card_id
    WHERE s.sold_on < k.cut_at;

    DELETE FROM sales s USING cut_list k
    WHERE k.card_id = s.card_id AND s.sold_on < k.cut_at;
    GET DIAGNOSTICS removed = ROW_COUNT;

    -- Belt and braces: if the delete and the export disagree, the CSV is not a
    -- faithful undo and the whole transaction must go.
    IF removed <> expected THEN
        RAISE EXCEPTION 'Deleted % rows but exported %; rolling back.', removed, expected;
    END IF;

    RAISE NOTICE 'Deleted % sale rows across % cards.', removed, (SELECT count(*) FROM cut_list);
END $$;

\echo ''
\echo '=== After the cut ==='
SELECT k.card_id, c.name, k.cut_at,
       min(s.sold_on) AS history_now_starts,
       count(*)       AS rows_remaining
FROM cut_list k
JOIN cards c ON c.id = k.card_id
JOIN sales s ON s.card_id = k.card_id
GROUP BY k.card_id, c.name, k.cut_at
ORDER BY k.card_id;

COMMIT;
