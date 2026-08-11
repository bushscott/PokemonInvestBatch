using PokemonInvestBatch.Application.Scheduling;
using PokemonInvestBatch.Domain.Parsing;

namespace PokemonInvestBatch.Application.Tests.Scheduling;

/// <summary>
/// Closed-loop replay of the hot-card incident class: scripted daily sales per
/// grade, a page that shows only the newest 30 rows per grade, and a scheduler
/// that revisits once staleness × estimated rate crosses half a bucket — the
/// same inequality VisitPriority.Score and the pool queries share. The
/// invariant under test: between any two visits, no grade may sell a full
/// bucket, because those rows would have scrolled off unseen.
/// </summary>
public class EstimatorSchedulingStressTests
{
    private static readonly DateOnly Start = new(2026, 6, 1);

    /// <summary>A card with no sale history yet: nothing can have scrolled
    /// off a page we are reading for the first time.</summary>
    private static readonly SalesOverlap NoHistory =
        new(new Dictionary<string, int>(), new Dictionary<string, int>());

    private static readonly VisitPriorityOptions Options = new();

    /// <summary>Everything one simulated crawl leaves behind.</summary>
    private sealed record Replay(List<int> VisitDays, List<int> Intervals);

    /// <summary>
    /// Walk the script a day at a time from the first visit: estimate the rate
    /// from the page as it looked at the last visit, revisit on the first day
    /// the burn inequality trips (or at the 30-day floor), and assert at every
    /// visit that no grade out-sold a bucket since the visit before.
    /// </summary>
    private static Replay Crawl(
        Dictionary<string, int[]> dailySalesByGrade, int firstVisitDay, VisitPriorityOptions? options = null)
    {
        var opts = options ?? Options;
        var horizon = dailySalesByGrade.Values.Max(days => days.Length);
        var visitDays = new List<int> { firstVisitDay };
        var intervals = new List<int>();
        var lastVisit = firstVisitDay;
        var rate = Estimate(dailySalesByGrade, firstVisitDay);

        for (var day = firstVisitDay + 1; day < horizon; day++)
        {
            var staleness = day - lastVisit;
            var due = rate > 0 && staleness * rate >= opts.SafetyFractionFor(rate) * SalesObservation.BucketCap;
            if (!due && staleness < opts.MaxDaysBetweenVisits)
            {
                continue;
            }

            foreach (var (grade, daily) in dailySalesByGrade)
            {
                var soldSinceLastVisit = daily
                    .Skip(lastVisit + 1)
                    .Take(day - lastVisit)
                    .Sum();
                Assert.True(
                    soldSinceLastVisit < SalesObservation.BucketCap,
                    $"{grade} sold {soldSinceLastVisit} in days {lastVisit + 1}..{day} — a full bucket rolled off unseen");
            }

            intervals.Add(day - lastVisit);
            visitDays.Add(day);
            lastVisit = day;
            rate = Estimate(dailySalesByGrade, day);
        }

        return new Replay(visitDays, intervals);
    }

    /// <summary>Render the newest-30-per-grade page as of a day, and estimate from it.</summary>
    private static double Estimate(Dictionary<string, int[]> dailySalesByGrade, int day)
    {
        var page = dailySalesByGrade
            .SelectMany(grade => Enumerable.Range(0, day + 1)
                .SelectMany(d => Enumerable.Range(0, grade.Value.ElementAtOrDefault(d))
                    .Select(i => new SaleRecord
                    {
                        Source = "ebay",
                        SourceId = $"{grade.Key}-{d}-{i}",
                        SoldOn = Start.AddDays(d),
                        GradeTier = grade.Key,
                        PriceCents = 100,
                        Title = "x",
                    }))
                .OrderByDescending(s => s.SoldOn)
                .Take(SalesObservation.BucketCap))
            .ToList();

        var now = new DateTimeOffset(Start.AddDays(day), new TimeOnly(12, 0), TimeSpan.Zero);
        return SalesObservation.From(page, NoHistory, now).SalesPerDay;
    }

    private static int[] Script(params (int count, int days)[] phases) =>
        [.. phases.SelectMany(p => Enumerable.Repeat(p.count, p.days))];

    [Fact]
    public void A_steady_seller_is_visited_well_inside_its_bucket()
    {
        var replay = Crawl(new() { ["Ungraded"] = Script((1, 60)) }, firstVisitDay: 5);

        Assert.True(replay.VisitDays.Count > 3);
    }

    [Fact]
    public void The_psyduck_shaped_burst_loses_nothing()
    {
        // The incident, in shape: a 2/day sleeper ramps to 7/day in its hottest
        // grade over five days, holds, then cools — while a second grade ticks
        // along slowly. The old 30-day-average estimator loses a bucket during
        // the ramp; the hottest-bucket estimator must not.
        var replay = Crawl(new()
        {
            ["PSA 10"] = Script((2, 20), (3, 1), (4, 1), (5, 1), (6, 1), (7, 12), (5, 2), (4, 2), (3, 2), (2, 3), (1, 15)),
            ["Ungraded"] = Script((1, 60)),
        }, firstVisitDay: 15);

        Assert.True(replay.VisitDays.Count > 5);
    }

    // The acceleration case deliberately lives in TieredBurnMarginTests instead
    // of here: this replay steps a whole day at a time, and the margin the
    // safety fraction buys is a fraction of one day. Rounding a 1.64-day revisit
    // up to 2 decides the outcome by itself, so a replay would report the
    // model's resolution rather than the scheduler's behaviour.

    [Fact]
    public void After_the_peak_the_visit_intervals_only_lengthen()
    {
        // A warm-up, a hard five-day burst, then silence. Every post-burst
        // re-estimate reads lower, so every interval is at least as long as
        // the one before — no oscillation.
        var replay = Crawl(
            new() { ["PSA 10"] = Script((2, 15), (10, 5), (0, 40)) },
            firstVisitDay: 15);

        var firstCoolingVisit = replay.VisitDays.FindIndex(d => d >= 20);
        var coolingIntervals = replay.Intervals.Skip(firstCoolingVisit).ToList();
        Assert.True(coolingIntervals.Count > 2);
        for (var i = 1; i < coolingIntervals.Count; i++)
        {
            Assert.True(
                coolingIntervals[i] >= coolingIntervals[i - 1],
                $"interval shrank from {coolingIntervals[i - 1]}d to {coolingIntervals[i]}d after the burst");
        }
    }
}
