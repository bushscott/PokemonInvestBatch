using PokemonInvestBatch.Application.Scheduling;
using PokemonInvestBatch.Domain.Parsing;

namespace PokemonInvestBatch.Application.Tests.Scheduling;

public class SalesObservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    private static SaleRecord Sale(string tier, DateOnly soldOn, string id) => new()
    {
        Source = "ebay",
        SourceId = id,
        SoldOn = soldOn,
        GradeTier = tier,
        PriceCents = 100,
        Title = "x",
    };

    [Fact]
    public void A_full_bucket_with_sales_newer_than_our_last_visit_is_at_cap()
    {
        // 30 rows, oldest 2026-07-09, last visited 2026-07-01: the bucket
        // rolled past what we saw — sales were provably missed.
        var sales = Enumerable.Range(0, 30)
            .Select(i => Sale("Grade 8", new DateOnly(2026, 7, 9).AddDays(i % 19), $"s{i}"))
            .ToList();

        var observation = SalesObservation.From(sales, lastVisitedAt: new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero), Now);

        Assert.True(observation.AnyBucketAtCap);
    }

    [Fact]
    public void A_full_bucket_whose_oldest_row_predates_our_visit_is_not_at_cap()
    {
        // 30 rows but the oldest is from before we last looked — nothing rolled off unseen.
        var sales = Enumerable.Range(0, 30)
            .Select(i => Sale("Grade 8", new DateOnly(2026, 6, 1).AddDays(i), $"s{i}"))
            .ToList();

        var observation = SalesObservation.From(sales, lastVisitedAt: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), Now);

        Assert.False(observation.AnyBucketAtCap);
    }

    [Fact]
    public void First_visits_are_never_at_cap()
    {
        var sales = Enumerable.Range(0, 30)
            .Select(i => Sale("Grade 8", new DateOnly(2026, 7, 9), $"s{i}"))
            .ToList();

        Assert.False(SalesObservation.From(sales, lastVisitedAt: null, Now).AnyBucketAtCap);
    }

    [Fact]
    public void Churn_is_sales_per_day_over_the_trailing_window()
    {
        // 15 sales in the trailing 30 days → 0.5/day.
        var sales = Enumerable.Range(0, 15)
            .Select(i => Sale("Ungraded", DateOnly.FromDateTime(Now.UtcDateTime).AddDays(-i), $"s{i}"))
            .Concat([Sale("Ungraded", new DateOnly(2024, 1, 1), "ancient")])
            .ToList();

        var observation = SalesObservation.From(sales, lastVisitedAt: null, Now);

        Assert.Equal(0.5, observation.SalesPerDay);
    }
}
