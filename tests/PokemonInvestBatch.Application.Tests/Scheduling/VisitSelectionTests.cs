using PokemonInvestBatch.Application.Scheduling;

namespace PokemonInvestBatch.Application.Tests.Scheduling;

/// <summary>
/// The ranking that decides which card gets the next request. These tests are
/// the reason the decision was pulled out of the lane: the tier order they pin
/// was once broken in production for days, and nothing could catch it while it
/// lived behind a database query.
/// </summary>
public class VisitSelectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    private static readonly VisitPriorityOptions Options = new();

    private static VisitCandidate Candidate(long id, CardVisitState state) =>
        new() { Id = id, State = state };

    /// <summary>Selling 6/day against a 30-row bucket burns its window in 5
    /// days; past half of that the card is due and its tier is the highest.</summary>
    private static CardVisitState BurnWindowDue() =>
        new() { LastVisitedAt = Now.AddDays(-4), ObservedSalesPerDay = 6 };

    [Fact]
    public void A_bench_retry_outranks_every_scored_candidate()
    {
        var choice = VisitSelection.Choose(
            benchRetryId: 42,
            [Candidate(1, BurnWindowDue())],
            Now,
            Options);

        Assert.Equal(VisitChoiceKind.RetryBenched, choice.Kind);
        Assert.Equal(42, choice.CardId);
    }

    [Fact]
    public void A_burn_window_due_card_outranks_the_unvisited_backlog()
    {
        // The regression this class exists for. The lane once short-circuited
        // to "unvisited first" before scoring, which made this case unreachable
        // while any unvisited card remained — and hot cards lost sales for it.
        var choice = VisitSelection.Choose(
            benchRetryId: null,
            [Candidate(7, BurnWindowDue())],
            Now,
            Options);

        Assert.Equal(VisitChoiceKind.Scored, choice.Kind);
        Assert.Equal(7, choice.CardId);
    }

    [Fact]
    public void A_merely_stale_card_yields_to_the_unvisited_backlog()
    {
        var choice = VisitSelection.Choose(
            benchRetryId: null,
            [Candidate(7, new CardVisitState { LastVisitedAt = Now.AddDays(-3) })],
            Now,
            Options);

        Assert.Equal(VisitChoiceKind.PreferUnvisited, choice.Kind);
    }

    [Fact]
    public void A_card_that_already_lost_sales_still_yields_to_the_unvisited_backlog()
    {
        // Deliberate tier order: a bucket at cap has already rolled rows off,
        // while a never-visited card can still be caught before it ever does.
        // Prevention outranks the already-burned.
        var choice = VisitSelection.Choose(
            benchRetryId: null,
            [Candidate(7, new CardVisitState { LastVisitedAt = Now.AddDays(-1), AnyBucketAtCap = true })],
            Now,
            Options);

        Assert.Equal(VisitChoiceKind.PreferUnvisited, choice.Kind);
    }

    [Fact]
    public void The_scored_runner_up_rides_along_as_the_unvisited_fallback()
    {
        // If the unvisited backlog turns out to be drained, the lane still has
        // somewhere to go rather than idling a whole cycle.
        var choice = VisitSelection.Choose(
            benchRetryId: null,
            [
                Candidate(1, new CardVisitState { LastVisitedAt = Now.AddDays(-3) }),
                Candidate(2, new CardVisitState { LastVisitedAt = Now.AddDays(-20) }),
            ],
            Now,
            Options);

        Assert.Equal(VisitChoiceKind.PreferUnvisited, choice.Kind);
        Assert.Equal(2, choice.CardId);
    }

    [Fact]
    public void An_empty_pool_asks_for_an_unvisited_card_and_offers_no_fallback()
    {
        var choice = VisitSelection.Choose(benchRetryId: null, [], Now, Options);

        Assert.Equal(VisitChoiceKind.PreferUnvisited, choice.Kind);
        Assert.Null(choice.CardId);
    }

    [Fact]
    public void The_most_overdue_burn_window_card_wins_among_several()
    {
        var choice = VisitSelection.Choose(
            benchRetryId: null,
            [
                Candidate(1, new CardVisitState { LastVisitedAt = Now.AddDays(-4), ObservedSalesPerDay = 6 }),
                Candidate(2, new CardVisitState { LastVisitedAt = Now.AddDays(-9), ObservedSalesPerDay = 6 }),
            ],
            Now,
            Options);

        Assert.Equal(VisitChoiceKind.Scored, choice.Kind);
        Assert.Equal(2, choice.CardId);
    }
}
