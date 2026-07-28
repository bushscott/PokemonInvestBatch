using Microsoft.Extensions.Time.Testing;
using PokemonInvestBatch.Application.Crawling;

namespace PokemonInvestBatch.Application.Tests.Crawling;

public class PoliteGateTests
{
    [Fact]
    public async Task Spaces_requests_by_the_current_delay()
    {
        var clock = new FakeTimeProvider();
        var delay = new AdaptiveDelay(new AdaptiveDelayOptions());
        var gate = new PoliteGate(delay, clock);

        // First turn is immediate — nothing to space against yet.
        await gate.WaitTurnAsync(CancellationToken.None);

        var second = gate.WaitTurnAsync(CancellationToken.None);
        Assert.False(second.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(299));
        Assert.False(second.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(1));
        await second;
    }

    [Fact]
    public async Task First_turn_never_waits()
    {
        var gate = new PoliteGate(new AdaptiveDelay(new AdaptiveDelayOptions()), new FakeTimeProvider());

        var first = gate.WaitTurnAsync(CancellationToken.None);

        Assert.True(first.IsCompleted);
        await first;
    }
}
