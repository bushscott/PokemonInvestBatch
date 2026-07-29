using PokemonInvestBatch.Application.Scheduling;

namespace PokemonInvestBatch.Application.Tests.Scheduling;

public class BurnWindowGuaranteeTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static readonly VisitPriorityOptions Options = new();

    private static CardVisitState Card(double? salesPerDay, int daysSinceVisit, bool atCap = false) =>
        new()
        {
            LastVisitedAt = Now.AddDays(-daysSinceVisit),
            ObservedSalesPerDay = salesPerDay,
            AnyBucketAtCap = atCap,
        };

    [Fact]
    public void A_card_nearing_its_burn_window_outranks_everything_but_discovery()
    {
        // 3 sales/day burns a 30-row bucket in 10 days; at half the window
        // (5 days) the card is due. Missing it would lose sales forever, so
        // prevention outranks even cap-hit revisits (already-burned cards).
        var due = VisitPriority.Score(Card(salesPerDay: 3, daysSinceVisit: 5), Now, Options);
        var capHit = VisitPriority.Score(Card(salesPerDay: 0.1, daysSinceVisit: 20, atCap: true), Now, Options);
        var starved = VisitPriority.Score(Card(salesPerDay: 0, daysSinceVisit: 35), Now, Options);

        Assert.True(due > capHit);
        Assert.True(due > starved);
    }

    [Fact]
    public void A_hot_card_recently_visited_scores_like_anyone_else()
    {
        // Two days into a ten-day burn window is not yet due: normal
        // staleness-times-churn scoring applies.
        var hot = VisitPriority.Score(Card(salesPerDay: 3, daysSinceVisit: 2), Now, Options);

        Assert.Equal(2 * (1 + 3), hot, precision: 5);
    }

    [Fact]
    public void A_cold_card_never_triggers_the_guarantee()
    {
        // No sales means no burn window — 29 days of staleness stays in the
        // base tier right up to the starvation floor.
        var cold = VisitPriority.Score(Card(salesPerDay: 0, daysSinceVisit: 29), Now, Options);

        Assert.True(cold < 1_000_000);
    }
}

public class VisitPriorityTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly VisitPriorityOptions Options = new();

    private static double Score(CardVisitState state) => VisitPriority.Score(state, Now, Options);

    [Fact]
    public void Never_visited_cards_come_before_everything_else()
    {
        var unvisited = Score(new CardVisitState { LastVisitedAt = null });
        var capHit = Score(new CardVisitState
        {
            LastVisitedAt = Now.AddDays(-40),
            AnyBucketAtCap = true,
            ObservedSalesPerDay = 10,
        });

        Assert.True(unvisited > capHit);
    }

    [Fact]
    public void A_full_bucket_overrides_any_staleness_or_churn()
    {
        // A full bucket with an oldest row newer than our last visit is proof
        // we lost sales — it overrides any staleness of cards that are not
        // themselves about to lose sales (those outrank even this).
        var capHit = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-1), AnyBucketAtCap = true });
        var veryStaleQuiet = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-25), ObservedSalesPerDay = 0 });

        Assert.True(capHit > veryStaleQuiet);
    }

    [Fact]
    public void Starved_cards_beat_busy_recent_cards()
    {
        // The floor: no card waits past MaxDaysBetweenVisits, however dull.
        // The busy card is fresh enough to be inside its burn-window safety
        // margin (10/day burns in 3 days; half is 1.5) — not yet due.
        var starved = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-31), ObservedSalesPerDay = 0 });
        var busyRecent = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-1), ObservedSalesPerDay = 10 });

        Assert.True(starved > busyRecent);
    }

    [Fact]
    public void Churn_orders_equally_stale_cards()
    {
        var hot = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-5), ObservedSalesPerDay = 2.5 });
        var cold = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-5), ObservedSalesPerDay = 0 });

        Assert.True(hot > cold);
    }

    [Fact]
    public void Staleness_alone_still_grows_priority()
    {
        var staler = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-9) });
        var fresher = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-3) });

        Assert.True(staler > fresher);
    }

    [Fact]
    public void Older_cap_hits_outrank_newer_cap_hits()
    {
        var older = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-6), AnyBucketAtCap = true });
        var newer = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-1), AnyBucketAtCap = true });

        Assert.True(older > newer);
    }
}
