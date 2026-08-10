using PokemonInvestBatch.Application.Scheduling;
using PokemonInvestBatch.Domain.Parsing;

namespace PokemonInvestBatch.Application.Tests.Scheduling;

public class SalesObservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    private static SaleRecord Sale(string tier, DateOnly soldOn, string id) => new()
    {
        Source = "ebay",
        SourceId = id,
        SoldOn = soldOn,
        GradeTier = tier,
        PriceCents = 100,
        Title = "x",
    };

    /// <summary>Count sales in one tier on the day that many days before Now.</summary>
    private static IEnumerable<SaleRecord> Daily(string tier, int daysAgo, int count) =>
        Enumerable.Range(0, count).Select(i => Sale(tier, Today.AddDays(-daysAgo), $"{tier}-{daysAgo}-{i}"));

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
    public void A_steady_daily_seller_reads_near_its_true_rate()
    {
        // One sale every day for 15 days. The fastest credible prefix (the 3
        // newest rows over 2 days) reads 1.5/day against a true 1.0/day — the
        // estimator's deliberate safe-side bias: a steady seller earns slightly
        // early visits, never late ones.
        var sales = Enumerable.Range(0, 15)
            .SelectMany(i => Daily("Ungraded", i, 1))
            .Concat([Sale("Ungraded", new DateOnly(2024, 1, 1), "ancient")])
            .ToList();

        var observation = SalesObservation.From(sales, lastVisitedAt: null, Now);

        Assert.Equal(1.5, observation.SalesPerDay);
    }

    [Fact]
    public void Psyduck_burst_schedules_a_revisit_within_two_days()
    {
        // The Aug 2026 incident, replayed: the hottest bucket's newest 30 rows
        // as the page showed them on visit day — 7 today, 7 yesterday, 6 two
        // days ago, 7 three days ago, 3 four days ago — plus a slower Ungraded
        // bucket. The 14 rows of the last two days read 14/day, so the
        // fast-track (half a 30-row bucket at that pace) lands ~1.07 days out.
        // The old card-wide 30-day average read these same 40 rows as 1.33/day
        // — an 11-day plan, and the sales were gone.
        var sales = Daily("PSA 10", 0, 7)
            .Concat(Daily("PSA 10", 1, 7))
            .Concat(Daily("PSA 10", 2, 6))
            .Concat(Daily("PSA 10", 3, 7))
            .Concat(Daily("PSA 10", 4, 3))
            .Concat(Enumerable.Range(0, 10).SelectMany(i => Daily("Ungraded", i * 3, 1)))
            .ToList();

        var observation = SalesObservation.From(sales, lastVisitedAt: Now.AddDays(-3.46), Now);

        var options = new VisitPriorityOptions();
        var revisitDays = options.BurnWindowSafetyFraction * SalesObservation.BucketCap / observation.SalesPerDay;

        Assert.Equal(14.0, observation.SalesPerDay);
        Assert.True(revisitDays <= 2.0, $"revisit in {revisitDays:F2}d — must be within 2");
    }

    [Fact]
    public void A_single_sale_yesterday_is_not_a_panic()
    {
        // One row is indistinguishable from one collector; it earns the steady
        // 1/30 rate (a 30-day-floor revisit), not a 1/day alarm.
        var observation = SalesObservation.From([.. Daily("Grade 9", 1, 1)], lastVisitedAt: null, Now);

        Assert.Equal(1 / 30.0, observation.SalesPerDay);
    }

    [Fact]
    public void Two_same_day_sales_are_still_not_a_burst()
    {
        var observation = SalesObservation.From([.. Daily("Grade 9", 0, 2)], lastVisitedAt: null, Now);

        Assert.Equal(2 / 30.0, observation.SalesPerDay);
    }

    [Fact]
    public void A_full_bucket_rate_is_rows_over_its_visible_span()
    {
        // 30 rows at a true 3/day over the last 10 days. The old formula read
        // a full bucket as 30/30 = 1/day no matter how fast it filled; the
        // span-based rate reads the days the rows actually cover.
        var sales = Enumerable.Range(1, 10)
            .SelectMany(d => Daily("PSA 10", d, 3))
            .ToList();

        var observation = SalesObservation.From(sales, lastVisitedAt: null, Now);

        Assert.Equal(3.0, observation.SalesPerDay);
    }

    [Fact]
    public void The_hottest_bucket_sets_the_card_rate()
    {
        // One hot bucket at 6/day; three tepid buckets that would sum higher
        // than the hot one. Buckets cap independently, so the card runs on the
        // hottest bucket's clock — max, never sum.
        var sales = Daily("PSA 10", 0, 3).Concat(Daily("PSA 10", 1, 3))
            .Concat(new[] { "Grade 9", "Grade 8", "Ungraded" }
                .SelectMany(tier => Enumerable.Range(0, 12).SelectMany(i => Daily(tier, 1 + i * 2, 1))))
            .ToList();

        var observation = SalesObservation.From(sales, lastVisitedAt: null, Now);

        Assert.Equal(6.0, observation.SalesPerDay);
    }

    [Fact]
    public void Rows_older_than_the_window_do_not_inflate_a_full_bucket()
    {
        // 10 rows inside the 30-day window, 20 stale rows beyond it. Only the
        // in-window rows may set the pace: 10 rows spanning 29 days, not 30
        // rows spanning 50.
        var sales = Enumerable.Range(20, 10).SelectMany(d => Daily("Grade 8", d, 1))
            .Concat(Enumerable.Range(31, 20).SelectMany(d => Daily("Grade 8", d, 1)))
            .ToList();

        var observation = SalesObservation.From(sales, lastVisitedAt: null, Now);

        Assert.Equal(10 / 29.0, observation.SalesPerDay);
    }

    [Fact]
    public void After_a_burst_ends_the_rate_decays_without_oscillation()
    {
        // A two-day 14-sale burst, then silence. Re-estimated as time passes,
        // the rate must only fall — the revisit interval lengthens monotonically,
        // with no stored state to ring or overshoot.
        var burst = Daily("PSA 10", 0, 7).Concat(Daily("PSA 10", 1, 7)).ToList();

        var rates = new[] { 0, 2, 6, 10, 35 }
            .Select(daysLater => SalesObservation.From(burst, lastVisitedAt: null, Now.AddDays(daysLater)).SalesPerDay)
            .ToList();

        for (var i = 1; i < rates.Count; i++)
        {
            Assert.True(rates[i] < rates[i - 1], $"rate rose from {rates[i - 1]:F2} to {rates[i]:F2} at step {i}");
        }

        Assert.Equal(0, rates[^1]);
    }
}
