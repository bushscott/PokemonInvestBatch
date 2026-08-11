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
        // The busy card must be fresh enough to still be inside its burn-window
        // margin, or it belongs in the burn tier and outranks the floor by
        // design — 5/day burns a 30-row bucket in 6 days, so at 0.3 it is not
        // due until 1.8.
        var busy = new CardVisitState { LastVisitedAt = Now.AddDays(-1), ObservedSalesPerDay = 5 };

        // Stated as a precondition so that tightening the dial again fails here
        // with a reason rather than looking like the tier order broke.
        Assert.True(
            Score(busy) < 3_000_000,
            "fixture is stale: the busy card is now burn-due, so pick a fresher one");

        var starved = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-31), ObservedSalesPerDay = 0 });

        Assert.True(starved > Score(busy));
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

/// <summary>
/// Which burn-due card goes first. The tier is entered by an inequality but
/// served through a bounded window, so under any backlog the order inside it
/// decides who actually gets visited — and the guarantee is about rows rolling
/// off a bucket, not about days on a clock.
/// </summary>
public class BurnDueOrderingTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    private static readonly VisitPriorityOptions Options = new();

    private static double Score(double salesPerDay, double daysSinceVisit) =>
        VisitPriority.Score(
            new CardVisitState
            {
                LastVisitedAt = Now.AddDays(-daysSinceVisit),
                ObservedSalesPerDay = salesPerDay,
            },
            Now,
            Options);

    [Fact]
    public void The_card_closest_to_rolling_goes_first_however_recently_it_was_visited()
    {
        // Kecleon #88 on 2026-08-11, in its real numbers. It sold 7/day and was
        // last seen 3.9 days back — 27 of its 30 rows burned, hours from losing
        // sales. Ahead of it sat 172 cards selling ~1.57/day last seen 12.2 days
        // back: three times the wait, but only 19 rows burned and a week of
        // slack. Ranking by days served all 172 first and Kecleon's page rolled
        // while it waited.
        var kecleon = Score(salesPerDay: 7.0, daysSinceVisit: 3.9);
        var slowButAncient = Score(salesPerDay: 1.57, daysSinceVisit: 12.2);

        Assert.True(kecleon > slowButAncient);
    }

    [Fact]
    public void Both_are_still_in_the_tier_that_outranks_everything_else()
    {
        // The reordering must not demote anyone out of the guarantee: a card
        // that waits its turn still beats every unvisited and starved card.
        var unvisited = VisitPriority.Score(new CardVisitState { LastVisitedAt = null }, Now, Options);

        Assert.True(Score(7.0, 3.9) > unvisited);
        Assert.True(Score(1.57, 12.2) > unvisited);
    }

    [Fact]
    public void Equally_burned_cards_tie_however_differently_they_got_there()
    {
        // Naming what this ranking cannot see, so a future change knows what it
        // is buying. Both cards have burned 20 of 30 rows, so both score the
        // same — but the faster one has ten rows left at 2/day and rolls in
        // five days, while the slower one has ten at 1/day and rolls in ten.
        // Days-to-roll would separate them; rows-burned is what the pool's
        // bounded window admits on, and the two must agree. The tie is
        // deliberate, and it is not what cost Kecleon its rows.
        var slowAndAncient = Score(salesPerDay: 1.0, daysSinceVisit: 20.0);
        var fastAndRecent = Score(salesPerDay: 2.0, daysSinceVisit: 10.0);

        Assert.Equal(slowAndAncient, fastAndRecent, precision: 9);
    }
}

/// <summary>
/// The revisit margin is not one number any more. A card fast enough to roll a
/// bucket is due earlier in its burn window than a cold one, because the margin
/// exists to absorb a card getting hotter between visits and only a card that
/// sells can do that. Spending the same margin on a card selling 0.02/day buys
/// nothing and costs five times as many visits corpus-wide.
/// </summary>
public class TieredBurnMarginTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly VisitPriorityOptions Options = new();

    private static double Score(double salesPerDay, double daysSinceVisit) =>
        VisitPriority.Score(
            new CardVisitState
            {
                LastVisitedAt = Now.AddDays(-daysSinceVisit),
                ObservedSalesPerDay = salesPerDay,
            },
            Now,
            Options);

    private const double BurnDueTier = 3_000_000;

    [Fact]
    public void A_hot_card_is_due_at_three_tenths_of_its_burn_window()
    {
        // 3/day burns a 30-row bucket in 10 days. Due at 3, not at 2.9.
        Assert.True(Score(3, 3.0) >= BurnDueTier);
        Assert.True(Score(3, 2.9) < BurnDueTier);
    }

    [Fact]
    public void A_cold_card_keeps_the_original_half_window()
    {
        // 0.5/day burns a bucket in 60 days. Still due at 30, not at 29 — the
        // tighter margin must not leak onto cards that cannot lose rows, or the
        // corpus-wide visit cost lands without buying any protection.
        Assert.True(Score(0.5, 30.0) >= BurnDueTier);
        Assert.True(Score(0.5, 29.0) < BurnDueTier);
    }

    [Fact]
    public void The_threshold_itself_earns_the_tighter_margin()
    {
        // Exactly at the threshold counts as hot: 1/day burns a bucket in 30
        // days, so the tighter margin puts it due at 9 rather than 15.
        Assert.True(Score(1.0, 9.0) >= BurnDueTier);

        // A shade under keeps the old margin and is not yet due at 9.
        Assert.True(Score(0.99, 9.0) < BurnDueTier);
    }

    /// <summary>
    /// Days until the scheduler is due back, given the rate it read off the page.
    /// </summary>
    private static double RevisitDue(double observedRate, VisitPriorityOptions options) =>
        SalesObservation.BucketCap / observedRate * options.SafetyFractionFor(observedRate);

    /// <summary>Days until a bucket actually rolls, at the rate the card really
    /// went on to sell at — which the page had no way of showing us.</summary>
    private static double RollsAfter(double trueRate) => SalesObservation.BucketCap / trueRate;

    [Fact]
    public void The_margin_is_exactly_the_acceleration_it_can_absorb()
    {
        // The whole point of the safety fraction, stated as the property it
        // actually has: revisiting at f of the burn window survives a card
        // getting up to 1/f times hotter between visits. Everything about
        // choosing a fraction follows from this one relation.
        foreach (var (fraction, survives) in new[]
                     { (0.5, 2.0), (0.4, 2.5), (0.33, 1 / 0.33), (0.3, 1 / 0.3), (0.25, 4.0) })
        {
            var options = new VisitPriorityOptions { HotBurnWindowSafetyFraction = fraction };
            const double observed = 7.33;

            // Just inside the ratio it can absorb: the visit lands first.
            Assert.True(RevisitDue(observed, options) < RollsAfter(observed * survives * 0.99));

            // Just outside it: the bucket rolls first.
            Assert.True(RevisitDue(observed, options) > RollsAfter(observed * survives * 1.01));
        }
    }

    [Fact]
    public void The_gardevior_acceleration_is_caught_at_four_tenths_and_missed_at_half()
    {
        // The 2026-08-10 loss in its real numbers. Mega Gardevior EX #32 read
        // 7.33/day off a page whose two most recent days were its quietest, then
        // ran at ~15/day — a 2.05x acceleration nothing on that page predicted.
        // Half a burn window absorbs 2.0x, so it missed by about an hour; four
        // tenths absorbs 2.5x and arrives with roughly nine hours to spare.
        const double observed = 7.33;
        const double actual = 15.0;
        var rolls = RollsAfter(actual);

        var half = new VisitPriorityOptions { HotBurnWindowSafetyFraction = 0.5 };
        Assert.True(RevisitDue(observed, half) > rolls);

        // Pinned at 0.4 explicitly rather than at whatever the default is today:
        // this test is the record of why 0.4 was chosen over 0.5, and that record
        // must stay true after the dial moves on.
        var fourTenths = new VisitPriorityOptions { HotBurnWindowSafetyFraction = 0.4 };
        Assert.True(RevisitDue(observed, fourTenths) < rolls);

        // Nine hours is thin but real. Naming it here means a future change to
        // the fraction has to confront how much margin it is spending.
        var spareHours = (rolls - RevisitDue(observed, fourTenths)) * 24;
        Assert.InRange(spareHours, 8.0, 10.0);

        // Whatever the dial is set to now, it may not regress this incident.
        Assert.True(RevisitDue(observed, Options) <= RevisitDue(observed, fourTenths));
    }

    [Fact]
    public void A_threefold_acceleration_is_caught_at_three_tenths_and_missed_at_four()
    {
        // Why the dial moved to 0.3 on 2026-08-11. Pikachu #1 is not the case
        // for it — that loss was a deflated estimate, not an acceleration past
        // 2.5x, and the reprice is what fixes it. The case is the one the
        // corpus keeps making: a card's rate can move further than any reading
        // of its page predicts, and 0.4 stops covering that at 2.5x.
        //
        // Measured cost of the move: burn-tier demand 4,437 -> 4,952 visits/day
        // against a ~8,400/day polite ceiling, with the 30-day floor already
        // counted in both. The crawl has the room.
        const double observed = 6.0;
        const double actual = 18.0;   // 3x — inside what 0.3 absorbs, past 0.4
        var rolls = RollsAfter(actual);

        var looser = new VisitPriorityOptions { HotBurnWindowSafetyFraction = 0.4 };
        Assert.True(RevisitDue(observed, looser) > rolls, "0.4 must miss a 3x acceleration");

        Assert.True(RevisitDue(observed, Options) < rolls, "the shipped default must catch it");
    }

    [Fact]
    public void The_fraction_is_a_knob_and_turning_it_down_pulls_the_visit_in()
    {
        // Turning HotBurnWindowSafetyFraction down is the agreed response to
        // another acceleration-shaped loss, so it has to actually move the line.
        var tighter = new VisitPriorityOptions { HotBurnWindowSafetyFraction = 0.25 };

        Assert.Equal(0.25, tighter.SafetyFractionFor(3));
        Assert.Equal(0.3, Options.SafetyFractionFor(3));

        // Unchanged for cards below the threshold, whichever way the knob goes.
        Assert.Equal(0.5, tighter.SafetyFractionFor(0.5));
    }
}
