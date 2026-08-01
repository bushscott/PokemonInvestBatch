using PokemonInvestBatch.Application.Scheduling;

namespace PokemonInvestBatch.Application.Tests.Scheduling;

public class BenchRecheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private static BenchedCandidate Benched(long id, TimeSpan comebackIn) => new()
    {
        Id = id,
        QuarantinedUntil = Now + comebackIn,
    };

    [Fact]
    public void A_benched_card_is_retried_without_any_waiting_period()
    {
        var recheck = new BenchRecheck(Interval);

        Assert.Equal(7L, recheck.TrySelect([Benched(7, TimeSpan.FromDays(1))], Now));
    }

    [Fact]
    public void A_successful_retry_keeps_the_slot_open_so_the_queue_drains()
    {
        var recheck = new BenchRecheck(Interval);
        recheck.TrySelect([Benched(1, TimeSpan.FromDays(1)), Benched(2, TimeSpan.FromDays(2))], Now);
        recheck.RecordSuccess();

        // The very next pick can retry the next benched card — a fix drains
        // the whole queue back-to-back, not one card per interval.
        Assert.Equal(2L, recheck.TrySelect([Benched(2, TimeSpan.FromDays(2))], Now.AddSeconds(10)));
    }

    [Fact]
    public void A_failed_retry_stands_the_recheck_down_for_the_interval()
    {
        var recheck = new BenchRecheck(Interval);
        recheck.TrySelect([Benched(1, TimeSpan.FromDays(1))], Now);
        recheck.RecordFailure(Now);

        Assert.Null(recheck.TrySelect([Benched(2, TimeSpan.FromDays(2))], Now.AddMinutes(9)));
        Assert.Equal(2L, recheck.TrySelect([Benched(2, TimeSpan.FromDays(2))], Now.AddMinutes(10)));
    }

    [Fact]
    public void A_success_reopens_a_slot_a_failure_had_closed()
    {
        var recheck = new BenchRecheck(Interval);
        recheck.RecordFailure(Now);
        recheck.RecordSuccess();

        Assert.Equal(1L, recheck.TrySelect([Benched(1, TimeSpan.FromDays(1))], Now.AddSeconds(1)));
    }

    [Fact]
    public void The_soonest_comeback_goes_first_so_failures_rotate_to_the_back()
    {
        // A failed retry's doubled sentence gives it the latest comeback of
        // the bunch — ordering by soonest comeback means the recheck moves on
        // to the others instead of fixating on the card that just failed.
        var recheck = new BenchRecheck(Interval);
        var benched = new[]
        {
            Benched(1, TimeSpan.FromDays(4)),
            Benched(2, TimeSpan.FromDays(1)),
            Benched(3, TimeSpan.FromDays(2)),
        };

        Assert.Equal(2L, recheck.TrySelect(benched, Now));
    }

    [Fact]
    public void An_empty_bench_selects_nothing()
    {
        var recheck = new BenchRecheck(Interval);

        Assert.Null(recheck.TrySelect([], Now));
    }
}
