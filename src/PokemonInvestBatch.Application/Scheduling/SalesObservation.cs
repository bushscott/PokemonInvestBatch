using PokemonInvestBatch.Domain.Parsing;

namespace PokemonInvestBatch.Application.Scheduling;

/// <summary>
/// What one visit's sales tell the scheduler: the scheduling rate — the hottest
/// grade bucket's observed fill rate in sales/day — and whether a grade bucket
/// provably rolled sales off unseen. The site keeps only the newest ~30 sales
/// per grade, so the bucket that fills fastest is the one that loses data
/// first; the card is revisited on that bucket's clock, never the card-wide
/// average's. A full bucket whose oldest row is newer than our previous visit
/// means sales were missed — that card is "at cap" and jumps the scheduling
/// order until its buckets calm down.
/// </summary>
public sealed record SalesObservation
{
    /// <summary>Graded buckets cap at 30 rows on the live site.</summary>
    public const int BucketCap = 30;

    /// <summary>Trailing window for the churn measure.</summary>
    public const int ChurnWindowDays = 30;

    /// <summary>Fewest rows a burst needs before it may set the pace: one or
    /// two same-day rows are indistinguishable from a single collector listing
    /// duplicates, so smaller prefixes fall through to the steady rate.</summary>
    public const int MinBurstRows = 3;

    public required bool AnyBucketAtCap { get; init; }

    /// <summary>The hottest grade bucket's fill rate, sales/day — the pace the
    /// scheduler must beat to see every row before it scrolls off.</summary>
    public required double SalesPerDay { get; init; }

    public static SalesObservation From(
        IReadOnlyList<SaleRecord> sales,
        DateTimeOffset? lastVisitedAt,
        DateTimeOffset now)
    {
        var anyAtCap = false;
        if (lastVisitedAt is { } visited)
        {
            var lastVisitDate = DateOnly.FromDateTime(visited.UtcDateTime);
            anyAtCap = sales
                .GroupBy(s => s.GradeTier)
                .Any(bucket => bucket.Count() >= BucketCap && bucket.Min(s => s.SoldOn) > lastVisitDate);
        }

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
            AnyBucketAtCap = anyAtCap,
            SalesPerDay = salesPerDay,
        };
    }

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
