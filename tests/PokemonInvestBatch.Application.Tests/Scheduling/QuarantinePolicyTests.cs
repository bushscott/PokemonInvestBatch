using PokemonInvestBatch.Application.Scheduling;

namespace PokemonInvestBatch.Application.Tests.Scheduling;

public class QuarantinePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Two_failures_are_bad_luck_not_poison()
    {
        Assert.Null(QuarantinePolicy.QuarantineUntil(failureStreak: 1, Now));
        Assert.Null(QuarantinePolicy.QuarantineUntil(failureStreak: 2, Now));
    }

    [Fact]
    public void The_third_consecutive_failure_quarantines_for_a_day()
    {
        Assert.Equal(Now.AddDays(1), QuarantinePolicy.QuarantineUntil(failureStreak: 3, Now));
    }

    [Fact]
    public void Each_further_failure_doubles_the_sentence()
    {
        Assert.Equal(Now.AddDays(2), QuarantinePolicy.QuarantineUntil(failureStreak: 4, Now));
        Assert.Equal(Now.AddDays(4), QuarantinePolicy.QuarantineUntil(failureStreak: 5, Now));
        Assert.Equal(Now.AddDays(8), QuarantinePolicy.QuarantineUntil(failureStreak: 6, Now));
    }

    [Fact]
    public void The_sentence_caps_at_thirty_days()
    {
        // A permanently delisted card settles into a monthly probe — cheap
        // enough to notice if the page ever comes back, quiet otherwise.
        Assert.Equal(Now.AddDays(30), QuarantinePolicy.QuarantineUntil(failureStreak: 8, Now));
        Assert.Equal(Now.AddDays(30), QuarantinePolicy.QuarantineUntil(failureStreak: 50, Now));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    [InlineData(403)]
    public void Client_errors_are_the_cards_fault(int status)
    {
        Assert.True(QuarantinePolicy.IsCardAttributable(status));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public void Site_trouble_never_builds_a_streak(int status)
    {
        // A site outage must not quarantine whichever innocent cards happened
        // to be scheduled during it — the AIMD pause owns that failure mode.
        Assert.False(QuarantinePolicy.IsCardAttributable(status));
    }
}
