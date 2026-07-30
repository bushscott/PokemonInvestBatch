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

    /// <summary>
    /// Multiplicative tightening per clean response until the first trouble
    /// of the process lifetime. 1.0 disables slow start (pure additive).
    /// </summary>
    public double SlowStartFactor { get; init; } = 0.5;

    public int FailuresBeforePause { get; init; } = 3;
}

/// <summary>
/// The courtesy delay — the gap we leave between our requests to the site.
/// AIMD (additive-increase/multiplicative-decrease,
/// TCP's fairness trick) inverted for politeness: each clean response
/// tightens the delay 5s toward the 10s floor; any trouble doubles it toward
/// (or past, per Retry-After) the 300s ceiling. Starts at the ceiling every
/// process start, and — mirroring TCP slow start, also inverted — tightens
/// multiplicatively until the first trouble of the process lifetime, so a
/// deploy against a healthy site reaches the floor in ~10 minutes instead
/// of ~2.5h. Any trouble ends slow start for good.
/// Pure state — no clock, no I/O.
/// </summary>
public sealed class AdaptiveDelay(AdaptiveDelayOptions options)
{
    // The class's one lock. Three lanes report outcomes concurrently and the
    // metrics gauge reads from the collection thread; without this, a
    // failure's doubling can be overwritten by a racing success's tighten.
    // Everything under the lock is pure state — no I/O, no callbacks,
    // nothing that can block. Keep it that way.
    private readonly Lock _sync = new();

    private bool _slowStart = true;

    private int _consecutiveFailures;

    private TimeSpan _current = options.Ceiling;

    private bool _shouldPause;

    /// <summary>Strikes toward the pause — visible early warning before it trips.</summary>
    public int ConsecutiveFailures
    {
        get
        {
            lock (_sync)
            {
                return _consecutiveFailures;
            }
        }
    }

    /// <summary>Starts at the ceiling: a cold start is never a thundering herd.</summary>
    public TimeSpan Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public bool ShouldPause
    {
        get
        {
            lock (_sync)
            {
                return _shouldPause;
            }
        }
    }

    public void RecordSuccess(TimeSpan latency)
    {
        lock (_sync)
        {
            if (latency >= options.SlowResponseThreshold)
            {
                // Their server straining is partly us — back off unasked.
                BackOff(retryAfter: null);
                return;
            }

            _consecutiveFailures = 0;
            _shouldPause = false;
            var tightened = _current - options.DecreaseStep;
            if (_slowStart)
            {
                // Whichever tightens harder; with a factor of 1.0 the additive
                // step always wins, disabling slow start.
                var halved = TimeSpan.FromTicks((long)(_current.Ticks * options.SlowStartFactor));
                tightened = halved < tightened ? halved : tightened;
            }

            _current = tightened < options.Floor ? options.Floor : tightened;
        }
    }

    /// <summary>Timeouts and 5xx (other than 503).</summary>
    public void RecordFailure(TimeSpan? retryAfter = null)
    {
        lock (_sync)
        {
            BackOff(retryAfter);
            CountFailure();
        }
    }

    /// <summary>429/503 — an explicit "stop", answered with the ceiling.</summary>
    public void RecordRateLimited(TimeSpan? retryAfter = null)
    {
        lock (_sync)
        {
            _slowStart = false;
            _current = MaxWithRetryAfter(options.Ceiling, retryAfter);
            CountFailure();
        }
    }

    /// <summary>Callers hold the lock.</summary>
    private void BackOff(TimeSpan? retryAfter)
    {
        _slowStart = false;
        var increased = TimeSpan.FromTicks((long)(_current.Ticks * options.IncreaseFactor));
        var capped = increased > options.Ceiling ? options.Ceiling : increased;
        _current = MaxWithRetryAfter(capped, retryAfter);
    }

    private static TimeSpan MaxWithRetryAfter(TimeSpan computed, TimeSpan? retryAfter) =>
        retryAfter is { } demanded && demanded > computed ? demanded : computed;

    /// <summary>Callers hold the lock.</summary>
    private void CountFailure()
    {
        if (++_consecutiveFailures >= options.FailuresBeforePause)
        {
            _shouldPause = true;
        }
    }
}
