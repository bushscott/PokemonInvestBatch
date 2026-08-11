using PokemonInvestBatch.Domain.Parsing;

namespace PokemonInvestBatch.Application.Scheduling;

/// <summary>
/// What one visit's sales tell the scheduler: the scheduling rate — the hottest
/// grade bucket's observed fill rate in sales/day — and whether a grade bucket
/// provably rolled sales off unseen. The site keeps only the newest sales per
/// grade, so the bucket that fills fastest is the one that loses data first;
/// the card is revisited on that bucket's clock, never the card-wide average's.
/// A bucket whose page no longer shows a single row we already held means sales
/// were missed — that card is "at cap" and jumps the scheduling order until its
/// buckets calm down.
/// </summary>
public sealed record SalesObservation
{
    /// <summary>Graded buckets cap at 30 rows on the live site — the bucket
    /// size the scheduler paces against. Deliberately the smallest bucket the
    /// site serves rather than the largest: pacing a 50- or 60-row Ungraded
    /// bucket as if it held 30 buys extra visits, while the reverse loses
    /// sales. <see cref="SalesOverlap"/> is what decides whether a bucket
    /// actually rolled, and it needs no bucket size at all.</summary>
    public const int BucketCap = 30;

    /// <summary>Trailing window for the churn measure.</summary>
    public const int ChurnWindowDays = 30;

    /// <summary>Fewest rows a burst needs before it may set the pace: one or
    /// two same-day rows are indistinguishable from a single collector listing
    /// duplicates, so smaller prefixes fall through to the steady rate.</summary>
    public const int MinBurstRows = 3;

    /// <summary>The grade bucket that provably rolled sales off unseen, named as
    /// the page names it. Null when nothing was lost — which is what
    /// <see cref="AnyBucketAtCap"/> asks.</summary>
    public string? CappedTier { get; init; }

    public bool AnyBucketAtCap => CappedTier is not null;

    /// <summary>
    /// Rows the tightest bucket still had in common with our records — how much
    /// slack there was between keeping up and losing data. Zero means it rolled,
    /// which is the same event <see cref="CappedTier"/> reports; a small positive
    /// number is a near miss, the graduated warning that a cap-hit alone cannot
    /// give because it only fires once the rows are already gone.
    ///
    /// Null when no bucket could have rolled in the first place — either we held
    /// nothing there to overlap with (a first visit says nothing about how fast a
    /// bucket fills), or the page did not return a full one.
    ///
    /// That fullness condition is load-bearing, and leaving it out was a bug:
    /// a bucket the site is not truncating cannot lose rows however thin the
    /// overlap looks. Card 959249's Grade 5 bucket holds one lifetime sale, so
    /// its page came back with one row and no new ones — margin 1, and utterly
    /// safe. <see cref="SalesOverlap"/> needs no bucket size because zero overlap
    /// *proves* rows vanished at any size; predicting a future roll is the
    /// opposite problem and does need to know the page is full.
    ///
    /// <see cref="BucketCap"/> is the right floor for that and stays conservative
    /// for the variable-size Ungraded table: it is the smallest bucket the site
    /// serves, so no bucket showing fewer rows can be truncated, and a 50-row
    /// Ungraded page clears it comfortably.
    /// </summary>
    public int? NarrowestMargin { get; init; }

    /// <summary>The bucket <see cref="NarrowestMargin"/> describes.</summary>
    public string? NarrowestTier { get; init; }

    /// <summary>The hottest grade bucket's fill rate, sales/day — the pace the
    /// scheduler must beat to see every row before it scrolls off.</summary>
    public required double SalesPerDay { get; init; }

    public static SalesObservation From(
        IReadOnlyList<SaleRecord> sales,
        SalesOverlap overlap,
        DateTimeOffset now)
    {
        // A bucket rolled past us when its page and our records share nothing:
        // every row on it was new, and we did hold rows there before. One
        // surviving row proves the page still reaches back to something we had
        // already seen, so nothing scrolled off in between. Asking about
        // overlap rather than fullness is what makes this correct for a bucket
        // whose size we do not know — see SalesOverlap.
        var cappedTier = sales
            .GroupBy(s => s.GradeTier)
            .FirstOrDefault(bucket =>
                overlap.HeldBefore(bucket.Key) > 0
                && overlap.NewlyWritten(bucket.Key) >= DistinctRows(bucket))
            ?.Key;

        // The same overlap arithmetic the cap test uses, kept as a number instead
        // of collapsed to a yes/no: rows the page still shared with us. Two
        // conditions gate it, and both are needed — we must have held something
        // there to overlap with, and the page must have come back full, because a
        // bucket the site is not truncating has nothing to push off the end.
        var narrowest = sales
            .GroupBy(s => s.GradeTier)
            .Select(bucket => new
            {
                Tier = bucket.Key,
                Rows = DistinctRows(bucket),
                Margin = DistinctRows(bucket) - overlap.NewlyWritten(bucket.Key),
            })
            .Where(b => overlap.HeldBefore(b.Tier) > 0 && b.Rows >= BucketCap)
            .OrderBy(b => b.Margin)
            .FirstOrDefault();

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var windowStart = today.AddDays(-ChurnWindowDays);
        var salesPerDay = sales
            .Where(s => s.SoldOn > windowStart)
            .GroupBy(s => s.GradeTier)
            .Select(bucket => BucketRate(bucket.Select(s => s.SoldOn), today))
            .DefaultIfEmpty(0)
            .Max();

        return new SalesObservation
        {
            CappedTier = cappedTier,
            NarrowestMargin = narrowest?.Margin,
            NarrowestTier = narrowest?.Tier,
            SalesPerDay = salesPerDay,
        };
    }

    /// <summary>Rows the writer could possibly have inserted for this bucket.
    /// A page can list the same (source, source_id) twice — 14,083 same-visit
    /// twins across the corpus — and the second copy is dropped on conflict, so
    /// comparing against the raw row count would hide a genuine loss.</summary>
    private static int DistinctRows(IEnumerable<SaleRecord> bucket) =>
        bucket.Select(s => (s.Source, s.SourceId)).Distinct().Count();

    /// <summary>
    /// A bucket's fill rate: its steady rate over the whole window, or its
    /// fastest credible recent prefix — whichever is higher. The k newest rows
    /// spanning d days sold at k/d, so taking the best prefix is both
    /// cap-correction (a full bucket is measured over the days it actually
    /// covers, not a fixed 30) and burst-detection (yesterday's surge counts
    /// immediately) in one expression. The one-day divisor floor is the
    /// smallest window a date-only source can honestly express.
    /// </summary>
    private static double BucketRate(IEnumerable<DateOnly> soldOn, DateOnly today)
    {
        var newestFirst = soldOn.OrderDescending().ToList();
        var rate = (double)newestFirst.Count / ChurnWindowDays;
        for (var k = MinBurstRows; k <= newestFirst.Count; k++)
        {
            var spanDays = Math.Max(today.DayNumber - newestFirst[k - 1].DayNumber, 1);
            rate = Math.Max(rate, (double)k / spanDays);
        }

        return rate;
    }
}
