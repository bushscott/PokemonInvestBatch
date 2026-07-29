namespace PokemonInvestBatch.Application.Crawling;

/// <summary>Tuning for the AIMD politeness controller. Defaults are the agreed design.</summary>
public sealed record AdaptiveDelayOptions
{
    public TimeSpan Floor { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan Ceiling { get; init; } = TimeSpan.FromSeconds(300);

    /// <summary>Additive decrease per clean response.</summary>
    public TimeSpan DecreaseStep { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Multiplicative increase on trouble.</summary>
    public double IncreaseFactor { get; init; } = 2.0;

    /// <summary>A "success" slower than this is treated as trouble.</summary>
    public TimeSpan SlowResponseThreshold { get; init; } = TimeSpan.FromSeconds(5);

    public int FailuresBeforePause { get; init; } = 3;
}

/// <summary>
/// The courtesy delay — the gap we leave between our requests to the site.
/// AIMD (additive-increase/multiplicative-decrease,
/// TCP's fairness trick) inverted for politeness: each clean response
/// tightens the delay 5s toward the 10s floor; any trouble doubles it toward
/// (or past, per Retry-After) the 300s ceiling. Starts at the ceiling every
/// process start, so a deploy costs ~2.5h of slow ramp — by design.
/// Pure state — no clock, no I/O.
/// </summary>
public sealed class AdaptiveDelay(AdaptiveDelayOptions options)
{
    /// <summary>Strikes toward the pause — visible early warning before it trips.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>Starts at the ceiling: a cold start is never a thundering herd.</summary>
    public TimeSpan Current { get; private set; } = options.Ceiling;

    public bool ShouldPause { get; private set; }

    public void RecordSuccess(TimeSpan latency)
    {
        if (latency >= options.SlowResponseThreshold)
        {
            // Their server straining is partly us — back off unasked.
            BackOff(retryAfter: null);
            return;
        }

        ConsecutiveFailures = 0;
        ShouldPause = false;
        var tightened = Current - options.DecreaseStep;
        Current = tightened < options.Floor ? options.Floor : tightened;
    }

    /// <summary>Timeouts and 5xx (other than 503).</summary>
    public void RecordFailure(TimeSpan? retryAfter = null)
    {
        BackOff(retryAfter);
        CountFailure();
    }

    /// <summary>429/503 — an explicit "stop", answered with the ceiling.</summary>
    public void RecordRateLimited(TimeSpan? retryAfter = null)
    {
        Current = MaxWithRetryAfter(options.Ceiling, retryAfter);
        CountFailure();
    }

    private void BackOff(TimeSpan? retryAfter)
    {
        var increased = TimeSpan.FromTicks((long)(Current.Ticks * options.IncreaseFactor));
        var capped = increased > options.Ceiling ? options.Ceiling : increased;
        Current = MaxWithRetryAfter(capped, retryAfter);
    }

    private static TimeSpan MaxWithRetryAfter(TimeSpan computed, TimeSpan? retryAfter) =>
        retryAfter is { } demanded && demanded > computed ? demanded : computed;

    private void CountFailure()
    {
        if (++ConsecutiveFailures >= options.FailuresBeforePause)
        {
            ShouldPause = true;
        }
    }
}
