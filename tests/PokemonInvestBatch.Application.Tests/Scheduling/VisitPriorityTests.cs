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
        // One day into its window is not yet due — not by the fraction plan
        // and not by its band's interval ceiling either (3/day sits in the
        // 2-day band, so the old fixture of "two days in" is due now). The
        // precondition keeps this failing with a reason if a ceiling
        // tightens past it, instead of reading as the tier order breaking.
        Assert.True(1.0 < Options.DueAfterDays(3), "fixture is stale: one day now reaches the due line");

        var hot = VisitPriority.Score(Card(salesPerDay: 3, daysSinceVisit: 1), Now, Options);

        Assert.Equal(1 * (1 + 3), hot, precision: 5);
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
        // design — 5/day burns a 30-row bucket in 6 days, so the fraction
        // would allow 1.8 and the fast ceiling makes it due at 1.5.
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
        // 7.5/day burns a 30-row bucket in 4 days, so at three tenths the due
        // line is 1.2 — past it at 1.25, short of it at 1.15. The rate is
        // chosen fast enough that the fraction, not the band's interval
        // ceiling, is the binding line — below 6/day the 1.5-day ceiling
        // arrives first and this test would be measuring that instead.
        Assert.True(30.0 / 7.5 * 0.3 < Options.FastCeilingDays, "fixture must let the fraction bind");

        Assert.True(Score(7.5, 1.25) >= BurnDueTier);
        Assert.True(Score(7.5, 1.15) < BurnDueTier);
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
        options.DueAfterDays(observedRate);

    /// <summary>Options with the interval ceilings switched off, for the tests
    /// that are the historical record of choosing a fraction: they compare
    /// dial values against each other, and a ceiling that catches what the
    /// dial under test missed would silently rewrite that record.</summary>
    private static VisitPriorityOptions DialOnly(double hotFraction) => new()
    {
        HotBurnWindowSafetyFraction = hotFraction,
        FastCeilingDays = double.PositiveInfinity,
        HotCeilingDays = double.PositiveInfinity,
    };

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
            // Ceilings off: this states the dial's own property in isolation.
            var options = DialOnly(fraction);
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

        // Dial-only on both sides: this is the record of choosing 0.4 over
        // 0.5, made before interval ceilings existed — a ceiling would catch
        // what the 0.5 dial missed and silently rewrite the history.
        var half = DialOnly(0.5);
        Assert.True(RevisitDue(observed, half) > rolls);

        // Pinned at 0.4 explicitly rather than at whatever the default is today:
        // this test is the record of why 0.4 was chosen over 0.5, and that record
        // must stay true after the dial moves on.
        var fourTenths = DialOnly(0.4);
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

        var looser = DialOnly(0.4);
        Assert.True(RevisitDue(observed, looser) > rolls, "0.4 must miss a 3x acceleration");

        Assert.True(
            RevisitDue(observed, DialOnly(0.3)) < rolls,
            "three tenths must catch it on its own — this is the dial's record, ceilings excluded");
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

    [Fact]
    public void The_hot_ceiling_survives_a_jump_the_dial_cannot()
    {
        // The dial multiplies the estimate, so its protection scales with a
        // number that can simply be wrong — the shape of all five Aug 2026
        // losses. A ceiling never consults the estimate: a card selling at
        // least HotRateThreshold waits at most HotCeilingDays, whatever its
        // stored rate says, so loss is impossible below BucketCap/ceiling
        // (10/day at 3 days) however stale the estimate is.
        //
        // Raichu-class numbers: read 1.43/day, ran at 6 (a 4.2x jump — the
        // measured tail reaches 6.9x). The bucket rolls in 5 days; the dial
        // alone would come back in 6.3.
        const double observed = 1.43;
        const double actual = 6.0;
        var rolls = RollsAfter(actual);

        Assert.True(
            observed < Options.FastCeilingRate,
            "fixture must sit in the 3-day band, not the 2-day one");
        Assert.True(RevisitDue(observed, Options) < rolls, "the ceiling must beat the roll");
        Assert.True(
            SalesObservation.BucketCap / observed * Options.SafetyFractionFor(observed) > rolls,
            "the dial alone must miss — otherwise this test isn't about the ceiling");
    }

    [Fact]
    public void The_fast_ceiling_covers_the_band_the_hot_ceiling_cannot()
    {
        // Above 10/day a 3-day wait already loses rows, so cards selling at
        // least FastCeilingRate get FastCeilingDays instead: loss impossible
        // below 20/day. Gengar-class numbers: read 3/day, ran at 12.
        const double observed = 3.0;
        const double actual = 12.0;
        var rolls = RollsAfter(actual);

        Assert.True(RevisitDue(observed, Options) < rolls, "the fast band must beat the roll");
        Assert.True(
            SalesObservation.BucketCap / observed * Options.SafetyFractionFor(observed) > rolls,
            "the dial alone must miss — otherwise this test isn't about the ceiling");
    }

    [Fact]
    public void The_mewtwo_race_is_tied_at_two_days_and_won_at_a_day_and_a_half()
    {
        // Why FastCeilingDays moved to 1.5 on 2026-08-17, in the incident's
        // real numbers. Mewtwo & Mew GX #SM191 read ~3.5/day off a calm page —
        // the band where the ceiling and not the fraction is the binding
        // line — then its PSA 10 bucket ran at 15/day or past it: a full page
        // over a 2-day gap censors the measured rate at exactly the number
        // the old ceiling protected. The bucket rolled in 2.0 days and the
        // visit landed at two days plus sixteen seconds — the old ceiling was
        // precisely wide enough to lose the race, ~7 rows, found 2026-08-16.
        const double observed = 3.5;
        const double actual = 15.0;
        var rolls = RollsAfter(actual);

        // Pinned at 2.0 explicitly: this is the record of why 1.5 replaced it,
        // and the record must stay true if the ceiling ever moves again.
        var old = new VisitPriorityOptions { FastCeilingDays = 2.0 };
        Assert.True(RevisitDue(observed, old) >= rolls, "the 2-day ceiling must tie the roll — the tie is the incident");

        // The documented response to an acceleration loss — turning the dial
        // down — cannot reach a ceiling-bound card: even at 0.25 the fraction
        // asks for later than the old ceiling, so the ceiling still decides.
        var oldWithTighterDial = new VisitPriorityOptions
        {
            FastCeilingDays = 2.0,
            HotBurnWindowSafetyFraction = 0.25,
        };
        Assert.True(RevisitDue(observed, oldWithTighterDial) >= rolls, "the dial must not reach it — that is why the ceiling moved");

        // Whatever the ceiling is set to now, it may not regress this incident.
        Assert.True(RevisitDue(observed, Options) < rolls, "a day and a half must win the race");
    }

    [Fact]
    public void A_cold_card_gets_no_ceiling()
    {
        // Below the hot threshold a bucket needs a month-plus to roll, and
        // ceilings there would cost thousands of visits a day for cards that
        // cannot lose rows. The warm band (0.5-1/day) is a deliberate
        // deferral, recorded 2026-08-12: its only real-world loss (Raichu)
        // was below the threshold only because a repricing bug deflated it.
        Assert.Equal(30.0 / 0.9 * 0.5, Options.DueAfterDays(0.9), 5);
    }

    [Fact]
    public void A_near_miss_halves_the_next_interval_once()
    {
        // A page that came back almost entirely new is the last warning
        // before a roll — the estimate at the PREVIOUS visit was too low, so
        // the next interval gets half the plan. Self-clearing: the flag is
        // rewritten from the page at every visit.
        const double rate = 3.0;
        var planned = Options.DueAfterDays(rate);

        Assert.Equal(planned / 2, Options.DueAfterDays(rate, nearMiss: true), 5);
        Assert.Equal(planned, Options.DueAfterDays(rate, nearMiss: false), 5);

        // And Score must actually read it: same card, same staleness — due
        // with the near-miss flag, not due without it.
        var justPastHalf = planned / 2 * 1.01;
        var state = new CardVisitState
        {
            LastVisitedAt = Now.AddDays(-justPastHalf),
            ObservedSalesPerDay = rate,
        };

        Assert.True(Score(state with { NearMiss = true }) >= BurnDueTier);
        Assert.True(Score(state) < BurnDueTier);
    }

    private static double Score(CardVisitState state) => VisitPriority.Score(state, Now, Options);
}
