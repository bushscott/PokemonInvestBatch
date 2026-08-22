using PokemonInvestBatch.Domain.Parsing;

namespace PokemonInvestBatch.Application.Scheduling;

/// <summary>What kind of event emptied a capped bucket — the difference
/// between an alert that says money was missed and one that says the page
/// merely changed under us.</summary>
public enum CapClass
{
    /// <summary>The page holds fewer rows than a full bucket, so velocity
    /// cannot have pushed anything off — the site removed or replaced the
    /// rows we held. Expected loss: none; the "lost" rows are in our DB.</summary>
    PageRecomposed,

    /// <summary>One seller's inventory hitting the market at once: a step
    /// function, not a rate. No schedule beats a full-page step inside one
    /// interval, and the organic tail around it is small.</summary>
    BulkLiquidation,

    /// <summary>Real market demand outran the visit pace. The only class
    /// where rows representing genuine market activity are gone.</summary>
    OrganicBurst,
}

/// <summary>
/// Reads the capped bucket's own page rows and says which kind of turnover
/// capped it. Every August 2026 cap alert that reached a human turned out,
/// on manual triage, to be one of three shapes — and only one of them loses
/// data. The tells are pinned to the real pages in CapClassificationTests;
/// the thresholds are exactly loose enough to catch the four production
/// dumps and exactly tight enough that the two production organic bursts
/// (one of which carries genuine same-day id near-runs) stay organic.
/// </summary>
public static class CapClassification
{
    /// <summary>Numeric source ids this close together are one listing
    /// session: the production dumps cluster within hundreds while organic
    /// pages' nearest same-shard neighbors sit thousands apart. 10,000 was
    /// measured too loose — it pulls a real organic page to 47% blocked.</summary>
    public const int RunGap = 1000;

    /// <summary>Rows a run needs before it counts. Two adjacent ids are one
    /// seller selling twice, which any healthy page contains.</summary>
    public const int MinRunRows = 3;

    /// <summary>A dump owns the page when at least half of it carries the
    /// tell — blocked ids, or one price sold in one day.</summary>
    public const double DumpFraction = 0.5;

    public static CapClass Classify(IReadOnlyList<SaleRecord> cappedBucketRows)
    {
        var rows = cappedBucketRows
            .Select(s => (s.SourceId, s.PriceCents, s.SoldOn))
            .Distinct()
            .ToList();

        // The same fullness rule NarrowestMargin trusts, read in reverse: a
        // bucket the site is not truncating has nothing to scroll off, so a
        // zero-overlap page below the cap means our rows were removed at the
        // source, whatever the ids look like.
        if (rows.Count < SalesObservation.BucketCap)
        {
            return CapClass.PageRecomposed;
        }

        var bar = rows.Count * DumpFraction;
        if (BlockedRows(rows.Select(r => r.SourceId)) >= bar)
        {
            return CapClass.BulkLiquidation;
        }

        // The flat-price dump (30 rows at exactly $40.00): its listing
        // session spread ids too wide for the run test. One price alone is
        // not enough — cheap cards sell organically at sticky price points —
        // but one price AND one day is a single seller's fixed-price stack
        // clearing out, and that is a step however the ids fall.
        var onePrice = rows.CountBy(r => r.PriceCents).Max(g => g.Value) >= bar;
        var oneDay = rows.CountBy(r => r.SoldOn).Max(g => g.Value) >= bar;
        return onePrice && oneDay ? CapClass.BulkLiquidation : CapClass.OrganicBurst;
    }

    /// <summary>Rows sitting in sequential-id runs of <see cref="MinRunRows"/>
    /// or more. eBay ids shard by prefix and auction ids are "session-lot",
    /// so ids are grouped into comparable streams first; runs never form
    /// across streams, and non-numeric ids never form them at all.</summary>
    private static int BlockedRows(IEnumerable<string> sourceIds) =>
        sourceIds
            .Select(ToStream)
            .Where(s => s is not null)
            .GroupBy(s => s!.Value.Stream)
            .Sum(stream => RunsOf(stream.Select(s => s!.Value.Position)));

    /// <summary>An id's comparable stream and its position within it. Plain
    /// numeric ids share one stream; "session-lot" auction ids compare lot
    /// against lot within their session; anything else is incomparable.</summary>
    private static (string Stream, long Position)? ToStream(string sourceId)
    {
        if (long.TryParse(sourceId, out var position))
        {
            return ("", position);
        }

        var dash = sourceId.IndexOf('-');
        if (dash > 0
            && long.TryParse(sourceId[..dash], out _)
            && long.TryParse(sourceId[(dash + 1)..], out var lot))
        {
            return (sourceId[..dash], lot);
        }

        return null;
    }

    private static int RunsOf(IEnumerable<long> positions)
    {
        var sorted = positions.Order().ToList();
        var blocked = 0;
        var runStart = 0;
        for (var i = 1; i <= sorted.Count; i++)
        {
            if (i < sorted.Count && sorted[i] - sorted[i - 1] <= RunGap)
            {
                continue;
            }

            if (i - runStart >= MinRunRows)
            {
                blocked += i - runStart;
            }

            runStart = i;
        }

        return blocked;
    }
}
