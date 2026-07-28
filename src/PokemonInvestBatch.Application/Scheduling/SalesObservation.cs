using PokemonInvestBatch.Domain.Parsing;

namespace PokemonInvestBatch.Application.Scheduling;

/// <summary>
/// What one visit's sales tell the scheduler: observed churn, and whether a
/// grade bucket provably rolled sales off unseen (full bucket whose oldest
/// row is newer than our previous visit).
/// </summary>
public sealed record SalesObservation
{
    /// <summary>Graded buckets cap at 30 rows on the live site.</summary>
    public const int BucketCap = 30;

    /// <summary>Trailing window for the churn measure.</summary>
    public const int ChurnWindowDays = 30;

    public required bool AnyBucketAtCap { get; init; }

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

        var windowStart = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-ChurnWindowDays);
        var recentSales = sales.Count(s => s.SoldOn > windowStart);

        return new SalesObservation
        {
            AnyBucketAtCap = anyAtCap,
            SalesPerDay = (double)recentSales / ChurnWindowDays,
        };
    }
}
