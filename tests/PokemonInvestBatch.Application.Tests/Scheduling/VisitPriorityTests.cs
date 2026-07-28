using PokemonInvestBatch.Application.Scheduling;

namespace PokemonInvestBatch.Application.Tests.Scheduling;

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
        // we lost sales — the hard override from the design.
        var capHit = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-1), AnyBucketAtCap = true });
        var veryStale = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-90), ObservedSalesPerDay = 5 });

        Assert.True(capHit > veryStale);
    }

    [Fact]
    public void Starved_cards_beat_busy_recent_cards()
    {
        // The floor: no card waits past MaxDaysBetweenVisits, however dull.
        var starved = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-31), ObservedSalesPerDay = 0 });
        var busyRecent = Score(new CardVisitState { LastVisitedAt = Now.AddDays(-2), ObservedSalesPerDay = 10 });

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
