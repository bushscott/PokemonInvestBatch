using PokemonInvestBatch.Application.Scheduling;

namespace PokemonInvestBatch.Application.Tests.Scheduling;

public class SecondChanceTrickleTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    /// <summary>Benched an hour+ ago on a first (1-day) sentence — due now.</summary>
    private static BenchedCandidate Due(long id, TimeSpan benchedAgo) => new()
    {
        Id = id,
        FailureStreak = 3,
        QuarantinedUntil = Now - benchedAgo + TimeSpan.FromDays(1),
    };

    [Fact]
    public void A_due_benched_card_gets_the_retry_slot()
    {
        var trickle = new SecondChanceTrickle(Interval);

        Assert.Equal(7L, trickle.TrySelect([Due(7, TimeSpan.FromHours(2))], Now));
    }

    [Fact]
    public void A_freshly_benched_card_is_not_due_yet()
    {
        // Benched five minutes ago on a 1-day sentence: its second chance
        // comes at the one-hour mark, not immediately.
        var trickle = new SecondChanceTrickle(Interval);

        Assert.Null(trickle.TrySelect([Due(7, TimeSpan.FromMinutes(5))], Now));
    }

    [Fact]
    public void Selecting_a_card_closes_the_slot_for_the_interval()
    {
        var trickle = new SecondChanceTrickle(Interval);
        trickle.TrySelect([Due(7, TimeSpan.FromHours(2))], Now);

        Assert.Null(trickle.TrySelect([Due(8, TimeSpan.FromHours(3))], Now.AddMinutes(9)));
        Assert.Equal(8L, trickle.TrySelect([Due(8, TimeSpan.FromHours(3))], Now.AddMinutes(10)));
    }

    [Fact]
    public void Coming_up_empty_does_not_consume_the_slot()
    {
        var trickle = new SecondChanceTrickle(Interval);
        Assert.Null(trickle.TrySelect([], Now));

        // The slot is still open one second later — no benched card should
        // ever wait a full interval behind a no-op.
        Assert.Equal(7L, trickle.TrySelect([Due(7, TimeSpan.FromHours(2))], Now.AddSeconds(1)));
    }

    [Fact]
    public void The_longest_pending_second_chance_goes_first()
    {
        var trickle = new SecondChanceTrickle(Interval);
        var benched = new[]
        {
            Due(1, TimeSpan.FromHours(2)),
            Due(2, TimeSpan.FromHours(6)),
            Due(3, TimeSpan.FromHours(4)),
        };

        Assert.Equal(2L, trickle.TrySelect(benched, Now));
    }

    [Fact]
    public void A_heavier_streak_waits_longer_than_a_first_offense()
    {
        // Both benched 90 minutes ago; the streak-3 card (due at 1h) is due,
        // the streak-4 card (due at 2h) is not — so the first offender wins.
        var trickle = new SecondChanceTrickle(Interval);
        var benched = new[]
        {
            new BenchedCandidate
            {
                Id = 1,
                FailureStreak = 4,
                QuarantinedUntil = Now - TimeSpan.FromMinutes(90) + TimeSpan.FromDays(2),
            },
            Due(2, TimeSpan.FromMinutes(90)),
        };

        Assert.Equal(2L, trickle.TrySelect(benched, Now));
    }
}
