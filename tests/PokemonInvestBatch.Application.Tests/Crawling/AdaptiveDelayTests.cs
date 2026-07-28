using PokemonInvestBatch.Application.Crawling;

namespace PokemonInvestBatch.Application.Tests.Crawling;

public class AdaptiveDelayTests
{
    private static AdaptiveDelay NewDelay() => new(new AdaptiveDelayOptions());

    [Fact]
    public void Starts_at_the_ceiling_so_cold_starts_are_never_a_thundering_herd()
    {
        Assert.Equal(TimeSpan.FromSeconds(300), NewDelay().Current);
    }

    [Fact]
    public void Clean_responses_tighten_additively_toward_the_floor()
    {
        var delay = NewDelay();

        delay.RecordSuccess(latency: TimeSpan.FromMilliseconds(200));

        Assert.Equal(TimeSpan.FromSeconds(295), delay.Current);
    }

    [Fact]
    public void Never_tightens_below_the_ten_second_floor()
    {
        var delay = NewDelay();

        for (var i = 0; i < 1_000; i++)
        {
            delay.RecordSuccess(TimeSpan.FromMilliseconds(200));
        }

        Assert.Equal(TimeSpan.FromSeconds(10), delay.Current);
    }

    [Fact]
    public void Trouble_backs_off_multiplicatively()
    {
        var delay = NewDelay();
        TightenTo(delay, TimeSpan.FromSeconds(10));

        delay.RecordFailure();

        Assert.Equal(TimeSpan.FromSeconds(20), delay.Current);
    }

    [Fact]
    public void Backoff_never_exceeds_the_ceiling()
    {
        var delay = NewDelay();

        for (var i = 0; i < 20; i++)
        {
            delay.RecordFailure();
        }

        Assert.Equal(TimeSpan.FromSeconds(300), delay.Current);
    }

    [Fact]
    public void Rate_limiting_jumps_straight_to_the_ceiling()
    {
        // A 429/503 is an explicit "stop" — no gradual response.
        var delay = NewDelay();
        TightenTo(delay, TimeSpan.FromSeconds(10));

        delay.RecordRateLimited();

        Assert.Equal(TimeSpan.FromSeconds(300), delay.Current);
    }

    [Fact]
    public void Retry_after_beyond_the_ceiling_is_honoured()
    {
        var delay = NewDelay();

        delay.RecordRateLimited(retryAfter: TimeSpan.FromSeconds(600));

        Assert.Equal(TimeSpan.FromSeconds(600), delay.Current);
    }

    [Fact]
    public void Slow_responses_count_as_trouble_not_success()
    {
        // If their server is straining, we are part of the reason — back off
        // before they have to tell us.
        var delay = NewDelay();
        TightenTo(delay, TimeSpan.FromSeconds(10));

        delay.RecordSuccess(latency: TimeSpan.FromSeconds(6));

        Assert.Equal(TimeSpan.FromSeconds(20), delay.Current);
    }

    [Fact]
    public void Three_consecutive_failures_demand_a_pause()
    {
        var delay = NewDelay();

        delay.RecordFailure();
        delay.RecordFailure();
        Assert.False(delay.ShouldPause);

        delay.RecordFailure();
        Assert.True(delay.ShouldPause);
    }

    [Fact]
    public void A_success_resets_the_failure_streak()
    {
        var delay = NewDelay();
        delay.RecordFailure();
        delay.RecordFailure();

        delay.RecordSuccess(TimeSpan.FromMilliseconds(200));
        delay.RecordFailure();

        Assert.False(delay.ShouldPause);
    }

    private static void TightenTo(AdaptiveDelay delay, TimeSpan target)
    {
        while (delay.Current > target)
        {
            delay.RecordSuccess(TimeSpan.FromMilliseconds(200));
        }
    }
}
