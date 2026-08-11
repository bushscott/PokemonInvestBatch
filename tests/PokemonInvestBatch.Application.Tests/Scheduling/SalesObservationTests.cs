using PokemonInvestBatch.Application.Scheduling;
using PokemonInvestBatch.Domain.Parsing;

namespace PokemonInvestBatch.Application.Tests.Scheduling;

public class SalesObservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);

    /// <summary>A card with no sale history yet: nothing can have scrolled
    /// off a page we are reading for the first time.</summary>
    private static readonly SalesOverlap NoHistory =
        new(new Dictionary<string, int>(), new Dictionary<string, int>());

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

    /// <summary>A page of <paramref name="rows"/> rows in one tier, where we
    /// held <paramref name="heldBefore"/> rows there and <paramref name="written"/>
    /// of the page's rows turned out to be new.</summary>
    private static SalesOverlap Overlap(string tier, int heldBefore, int written) =>
        new(new Dictionary<string, int> { [tier] = heldBefore },
            new Dictionary<string, int> { [tier] = written });

    [Fact]
    public void A_bucket_sharing_no_row_with_our_records_is_at_cap()
    {
        // 30 rows on the page, we held 40 in that bucket, and all 30 were new:
        // the page no longer reaches back to anything we had, so whatever sold
        // in between scrolled off unseen.
        var sales = Enumerable.Range(0, 30)
            .Select(i => Sale("Grade 8", new DateOnly(2026, 7, 9).AddDays(i % 19), $"s{i}"))
            .ToList();

        var observation = SalesObservation.From(sales, Overlap("Grade 8", heldBefore: 40, written: 30), Now);

        Assert.True(observation.AnyBucketAtCap);
        Assert.Equal("Grade 8", observation.CappedTier);
    }

    [Fact]
    public void A_bucket_still_showing_a_row_we_already_had_is_not_at_cap()
    {
        // Same 30-row page, but one row was already ours. That single survivor
        // proves the page reaches back past our last visit, so nothing fell off
        // in between — no matter how full the bucket looks.
        var sales = Enumerable.Range(0, 30)
            .Select(i => Sale("Grade 8", new DateOnly(2026, 7, 9).AddDays(i % 19), $"s{i}"))
            .ToList();

        var observation = SalesObservation.From(sales, Overlap("Grade 8", heldBefore: 40, written: 29), Now);

        Assert.False(observation.AnyBucketAtCap);
        Assert.Null(observation.CappedTier);
    }

    [Fact]
    public void A_fifty_row_ungraded_page_is_judged_by_overlap_not_by_thirty()
    {
        // The false-alarm case that started this: Ungraded renders 50 or 60
        // rows where a graded bucket renders 30. Counting rows against a
        // 30-row cap called this "at cap" and mailed a sales-lost alert; the
        // page still shares 15 rows with our records, so nothing was lost.
        var sales = Enumerable.Range(0, 50)
            .Select(i => Sale("Ungraded", new DateOnly(2026, 7, 9).AddDays(i % 19), $"u{i}"))
            .ToList();

        var observation = SalesObservation.From(sales, Overlap("Ungraded", heldBefore: 60, written: 35), Now);

        Assert.False(observation.AnyBucketAtCap);
    }

    [Fact]
    public void A_tier_we_have_never_held_a_row_in_is_not_at_cap()
    {
        // A card's first-ever PSA 10 sales: every row is new, but there was no
        // history to scroll off. Zero overlap only means loss when we had
        // something to lose.
        var sales = Enumerable.Range(0, 30)
            .Select(i => Sale("PSA 10", new DateOnly(2026, 7, 9), $"p{i}"))
            .ToList();

        var observation = SalesObservation.From(sales, Overlap("PSA 10", heldBefore: 0, written: 30), Now);

        Assert.False(observation.AnyBucketAtCap);
    }

    [Fact]
    public void First_visits_are_never_at_cap()
    {
        var sales = Enumerable.Range(0, 30)
            .Select(i => Sale("Grade 8", new DateOnly(2026, 7, 9), $"s{i}"))
            .ToList();

        Assert.False(SalesObservation.From(sales, NoHistory, Now).AnyBucketAtCap);
    }

    [Fact]
    public void A_page_listing_the_same_sale_twice_still_reads_as_rolled()
    {
        // Pages do list a sale twice (14,083 same-visit twins across the
        // corpus). The duplicate is dropped on conflict, so the writer reports
        // fewer new rows than the page has rows — comparing against the raw
        // count would read that shortfall as overlap and miss a real loss.
        var sales = Enumerable.Range(0, 30)
            .Select(i => Sale("Grade 8", new DateOnly(2026, 7, 9), $"s{i}"))
            .Concat([Sale("Grade 8", new DateOnly(2026, 7, 9), "s0")])
            .ToList();

        var observation = SalesObservation.From(sales, Overlap("Grade 8", heldBefore: 40, written: 30), Now);

        Assert.Equal(31, sales.Count);
        Assert.True(observation.AnyBucketAtCap);
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

        var observation = SalesObservation.From(sales, NoHistory, Now);

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

        var observation = SalesObservation.From(sales, NoHistory, Now);

        var options = new VisitPriorityOptions();
        var revisitDays = options.BurnWindowSafetyFraction * SalesObservation.BucketCap / observation.SalesPerDay;

        Assert.Equal(14.0, observation.SalesPerDay);
        Assert.True(revisitDays <= 2.0, $"revisit in {revisitDays:F2}d — must be within 2");
    }

    /// <summary>Sales in one tier on an exact date, for replaying a real page.</summary>
    private static IEnumerable<SaleRecord> On(string tier, string soldOn, int count) =>
        Enumerable.Range(0, count)
            .Select(i => Sale(tier, DateOnly.Parse(soldOn), $"{tier}-{soldOn}-{i}"));

    [Fact]
    public void A_page_rated_three_days_after_it_was_fetched_reads_half_speed()
    {
        // The 2026-08-11 loss (card 3449670, Pikachu #1), replayed from the
        // ledger. This is the page as it stood at its 2026-08-07 21:16 visit:
        // 50 PSA 10 rows back to Jul 25, and an Ungraded bucket whose six Aug 6
        // sales are the hottest thing on the card.
        var page = On("PSA 10", "2026-08-07", 3)
            .Concat(On("PSA 10", "2026-08-06", 2)).Concat(On("PSA 10", "2026-08-05", 2))
            .Concat(On("PSA 10", "2026-08-04", 7)).Concat(On("PSA 10", "2026-08-03", 3))
            .Concat(On("PSA 10", "2026-08-02", 3)).Concat(On("PSA 10", "2026-07-30", 10))
            .Concat(On("PSA 10", "2026-07-29", 2)).Concat(On("PSA 10", "2026-07-28", 2))
            .Concat(On("PSA 10", "2026-07-27", 5)).Concat(On("PSA 10", "2026-07-26", 6))
            .Concat(On("PSA 10", "2026-07-25", 5))
            .Concat(On("Ungraded", "2026-08-06", 6)).Concat(On("Ungraded", "2026-08-03", 3))
            .Concat(On("Ungraded", "2026-08-01", 1)).Concat(On("Ungraded", "2026-07-30", 1))
            .ToList();

        var fetchedOn = new DateTimeOffset(2026, 8, 7, 21, 16, 0, TimeSpan.Zero);

        // Read on the day it was fetched, the page says 6/day.
        Assert.Equal(6.0, SalesObservation.From(page, NoHistory, fetchedOn).SalesPerDay);

        // Read three days later off stored history — which is what the
        // 2026-08-10 reprice did to 45,833 cards — the SAME rows say 3.125/day.
        // Nothing sold slower; the three days nobody looked are divided in as
        // days nothing sold. A rate computed off stored rows must be anchored to
        // the date they were captured, never to the date the job happens to run.
        var repricedLate = SalesObservation.From(page, NoHistory, fetchedOn.AddDays(3)).SalesPerDay;
        Assert.Equal(3.125, repricedLate);

        // Half the rate is double the burn window, and the bucket rolled inside
        // the difference. The card went on to sell ~12/day, which empties a
        // 30-row bucket 2.5 days after the visit.
        const double rollsAfterDays = SalesObservation.BucketCap / 12.0;
        var options = new VisitPriorityOptions();

        var onTime = SalesObservation.BucketCap / 6.0 * options.SafetyFractionFor(6.0);
        Assert.True(onTime < rollsAfterDays, $"honest rate must beat the roll: {onTime:F2}d");

        var deflated = SalesObservation.BucketCap / repricedLate * options.SafetyFractionFor(repricedLate);
        Assert.True(deflated > rollsAfterDays, $"deflated rate must miss it: {deflated:F2}d");
    }

    [Fact]
    public void A_single_sale_yesterday_is_not_a_panic()
    {
        // One row is indistinguishable from one collector; it earns the steady
        // 1/30 rate (a 30-day-floor revisit), not a 1/day alarm.
        var observation = SalesObservation.From([.. Daily("Grade 9", 1, 1)], NoHistory, Now);

        Assert.Equal(1 / 30.0, observation.SalesPerDay);
    }

    [Fact]
    public void Two_same_day_sales_are_still_not_a_burst()
    {
        var observation = SalesObservation.From([.. Daily("Grade 9", 0, 2)], NoHistory, Now);

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

        var observation = SalesObservation.From(sales, NoHistory, Now);

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

        var observation = SalesObservation.From(sales, NoHistory, Now);

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

        var observation = SalesObservation.From(sales, NoHistory, Now);

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
            .Select(daysLater => SalesObservation.From(burst, NoHistory, Now.AddDays(daysLater)).SalesPerDay)
            .ToList();

        for (var i = 1; i < rates.Count; i++)
        {
            Assert.True(rates[i] < rates[i - 1], $"rate rose from {rates[i - 1]:F2} to {rates[i]:F2} at step {i}");
        }

        Assert.Equal(0, rates[^1]);
    }
}

/// <summary>
/// The near-miss margin: how many rows the page still had in common with us,
/// kept as a number instead of collapsed to the at-cap yes/no. A cap hit can
/// only speak once rows are gone; this is the same arithmetic read early enough
/// to be a warning. Like SalesOverlap it never needs a bucket size, so it stays
/// honest for the Ungraded bucket whose size varies.
/// </summary>
public class NearMissMarginTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    private static SaleRecord Sale(string tier, int daysAgo, string id) => new()
    {
        Source = "ebay",
        SourceId = id,
        SoldOn = DateOnly.FromDateTime(Now.UtcDateTime).AddDays(-daysAgo),
        GradeTier = tier,
        PriceCents = 100,
        Title = "x",
    };

    private static List<SaleRecord> Page(string tier, int rows) =>
        [.. Enumerable.Range(0, rows).Select(i => Sale(tier, i % 5, $"{tier}-{i}"))];

    [Fact]
    public void A_page_that_barely_reached_back_reports_its_slack()
    {
        // 30 rows came back, 28 of them new: two rows of overlap were all that
        // stood between keeping up and losing data.
        var observation = SalesObservation.From(
            Page("PSA 10", 30),
            new SalesOverlap(
                new Dictionary<string, int> { ["PSA 10"] = 40 },
                new Dictionary<string, int> { ["PSA 10"] = 28 }),
            Now);

        Assert.Equal(2, observation.NarrowestMargin);
        Assert.Equal("PSA 10", observation.NarrowestTier);
        Assert.Null(observation.CappedTier);
    }

    [Fact]
    public void A_first_visit_cannot_near_miss()
    {
        // Everything on the page is new because we held nothing, which says
        // nothing about how fast the bucket fills. Reporting zero slack here
        // would make every card's first visit look like an emergency — and the
        // first visit is most cards' only visit.
        var observation = SalesObservation.From(
            Page("PSA 10", 30),
            new SalesOverlap(new Dictionary<string, int>(), new Dictionary<string, int> { ["PSA 10"] = 30 }),
            Now);

        Assert.Null(observation.NarrowestMargin);
        Assert.Null(observation.NarrowestTier);
        Assert.Null(observation.CappedTier);
    }

    [Fact]
    public void Zero_slack_is_the_cap_hit_itself()
    {
        // The two signals must agree at the boundary: no overlap left is not a
        // near miss but the loss, and CardVisitor reports it as such.
        var observation = SalesObservation.From(
            Page("PSA 10", 30),
            new SalesOverlap(
                new Dictionary<string, int> { ["PSA 10"] = 40 },
                new Dictionary<string, int> { ["PSA 10"] = 30 }),
            Now);

        Assert.Equal(0, observation.NarrowestMargin);
        Assert.Equal("PSA 10", observation.CappedTier);
    }

    [Fact]
    public void The_tightest_bucket_is_the_one_reported()
    {
        // A card is only as safe as its fastest-filling grade, so the margin
        // names the bucket with the least slack, not the card-wide average.
        List<SaleRecord> page = [.. Page("PSA 10", 30), .. Page("Ungraded", 50)];
        var observation = SalesObservation.From(
            page,
            new SalesOverlap(
                new Dictionary<string, int> { ["PSA 10"] = 40, ["Ungraded"] = 90 },
                new Dictionary<string, int> { ["PSA 10"] = 29, ["Ungraded"] = 20 }),
            Now);

        Assert.Equal(1, observation.NarrowestMargin);
        Assert.Equal("PSA 10", observation.NarrowestTier);
    }

    [Fact]
    public void A_bucket_the_site_is_not_truncating_cannot_near_miss()
    {
        // The false positive this rule was added for, in its real numbers. Card
        // 959249's Grade 5 bucket holds one lifetime sale, so the page returns
        // that single row and nothing new: margin 1, which looks alarming and is
        // completely safe. Nothing can be pushed off a page showing everything
        // there is.
        var observation = SalesObservation.From(
            Page("Grade 5", 1),
            new SalesOverlap(
                new Dictionary<string, int> { ["Grade 5"] = 1 },
                new Dictionary<string, int>()),
            Now);

        Assert.Null(observation.NarrowestMargin);
        Assert.Null(observation.NarrowestTier);
    }

    [Fact]
    public void A_thin_margin_on_a_short_page_is_still_not_a_near_miss()
    {
        // Same rule one row below the line: 29 rows cannot be a truncated graded
        // bucket, so however little overlap it shows, nothing rolled past us.
        var observation = SalesObservation.From(
            Page("PSA 10", 29),
            new SalesOverlap(
                new Dictionary<string, int> { ["PSA 10"] = 40 },
                new Dictionary<string, int> { ["PSA 10"] = 28 }),
            Now);

        Assert.Null(observation.NarrowestMargin);
    }

    [Fact]
    public void A_full_bucket_is_still_reported_when_a_short_one_is_tighter()
    {
        // The short bucket has the smaller margin but cannot roll; picking it
        // would hide the full bucket that genuinely nearly did.
        List<SaleRecord> page = [.. Page("Grade 5", 2), .. Page("PSA 10", 30)];
        var observation = SalesObservation.From(
            page,
            new SalesOverlap(
                new Dictionary<string, int> { ["Grade 5"] = 2, ["PSA 10"] = 40 },
                new Dictionary<string, int> { ["Grade 5"] = 0, ["PSA 10"] = 26 }),
            Now);

        Assert.Equal(4, observation.NarrowestMargin);
        Assert.Equal("PSA 10", observation.NarrowestTier);
    }

    [Fact]
    public void Ungraded_never_needs_its_true_page_size_to_be_known()
    {
        // The reverted UngradedBucketCap idea failed because the Ungraded table
        // renders 30, 50 or 60 rows depending on the page. Nothing here needs to
        // know which: the fullness gate is BucketCap, the smallest bucket the
        // site serves, so a 50-row page clears it without anyone deciding what
        // 50 means, and the margin itself is just rows we already held.
        var observation = SalesObservation.From(
            Page("Ungraded", 50),
            new SalesOverlap(
                new Dictionary<string, int> { ["Ungraded"] = 120 },
                new Dictionary<string, int> { ["Ungraded"] = 47 }),
            Now);

        Assert.Equal(3, observation.NarrowestMargin);
        Assert.Equal("Ungraded", observation.NarrowestTier);
    }
}
