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
    public void A_card_nearing_its_burn_window_outranks_everything_including_discovery()
    {
        // 3 sales/day burns a 30-row bucket in 10 days; at half the window
        // (5 days) the card is due. Missing it would lose sales forever, so
        // prevention outranks everything — even never-visited cards. A large
        // unvisited backlog (first pass, a freshly discovered set) must not
        // suspend the zero-missed-sales guarantee.
        var due = VisitPriority.Score(Card(salesPerDay: 3, daysSinceVisit: 5), Now, Options);
        var unvisited = VisitPriority.Score(new CardVisitState { LastVisitedAt = null }, Now, Options);
        var capHit = VisitPriority.Score(Card(salesPerDay: 0.1, daysSinceVisit: 20, atCap: true), Now, Options);
        var starved = VisitPriority.Score(Card(salesPerDay: 0, daysSinceVisit: 35), Now, Options);

        Assert.True(due > unvisited);
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

public class RefreshRequestTierTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    private static readonly VisitPriorityOptions Options = new();

    private static double Score(CardVisitState state) => VisitPriority.Score(state, Now, Options);

    [Fact]
    public void A_requested_card_outranks_the_unvisited_backlog()
    {
        // The ask jumps the discovery queue: a card another app wants fresh
        // goes before first-pass exploration, however large the backlog.
        var requested = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-1), RefreshRequested = true });
        var unvisited = Score(new CardVisitState { LastVisitedAt = null });

        Assert.True(requested > unvisited);
    }

    [Fact]
    public void A_requested_card_still_yields_to_a_burn_window_due_card()
    {
        // 3 sales/day burns a bucket in 10 days; at 5 the card is due, and
        // prevention outranks the ask — the caller waits one slot, the sales
        // are never lost.
        var requested = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-20), RefreshRequested = true });
        var due = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-5), ObservedSalesPerDay = 3 });

        Assert.True(due > requested);
    }

    [Fact]
    public void A_requested_burn_window_due_card_keeps_its_burn_window_rank()
    {
        // An ask must never demote the card it points at: requested-and-due
        // scores exactly as due, in the tier the guarantee owns.
        var requestedAndDue = Score(new CardVisitState
        {
            LastVisitedAt = Now.AddDays(-5),
            ObservedSalesPerDay = 3,
            RefreshRequested = true,
        });
        var dueAlone = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-5), ObservedSalesPerDay = 3 });

        Assert.Equal(dueAlone, requestedAndDue);
    }

    [Fact]
    public void A_requested_card_outranks_cap_hits_and_the_starved()
    {
        var requested = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-1), RefreshRequested = true });
        var capHit = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-40), AnyBucketAtCap = true });
        var starved = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-35) });

        Assert.True(requested > capHit);
        Assert.True(requested > starved);
    }

    [Fact]
    public void A_requested_never_visited_card_scores_the_requested_tier_not_the_unvisited_one()
    {
        // Being new to the corpus must not bury the ask under the backlog —
        // and the ask must still not outrank prevention.
        var requestedNew = Score(new CardVisitState { LastVisitedAt = null, RefreshRequested = true });
        var plainNew = Score(new CardVisitState { LastVisitedAt = null });
        var due = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-5), ObservedSalesPerDay = 3 });

        Assert.True(requestedNew > plainNew);
        Assert.True(due > requestedNew);
    }

    [Fact]
    public void Staler_requested_cards_go_first_among_equals()
    {
        var staler = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-9), RefreshRequested = true });
        var fresher = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-2), RefreshRequested = true });

        Assert.True(staler > fresher);
    }
}

public class VisitPriorityTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    private static readonly VisitPriorityOptions Options = new();

    private static double Score(CardVisitState state) => VisitPriority.Score(state, Now, Options);

    [Fact]
    public void Never_visited_cards_come_before_everything_except_a_due_card()
    {
        var unvisited = Score(new CardVisitState { LastVisitedAt = null });
        var due = Score(new CardVisitState
        {
            LastVisitedAt = Now.AddDays(-5),
            ObservedSalesPerDay = 3,
        });
        // No current sales, so the burn-window guarantee does not apply:
        // the cap flag alone (past loss, already burned) ranks below both.
        var capHit = Score(new CardVisitState
        {
            LastVisitedAt = Now.AddDays(-40),
            AnyBucketAtCap = true,
            ObservedSalesPerDay = 0,
        });

        Assert.True(due > unvisited);
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
