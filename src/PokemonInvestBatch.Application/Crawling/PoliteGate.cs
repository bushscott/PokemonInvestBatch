namespace PokemonInvestBatch.Application.Crawling;

/// <summary>
/// The single shared gate in front of pricecharting.com: every lane waits its
/// turn here, so the politeness budget is global no matter how many lanes run.
/// Image CDN fetches do not pass through this gate (different host). Express
/// visits deliberately bypass the wait — but they report in via
/// <see cref="RecordFetchNow"/>, so the next scheduled turn re-spaces around
/// them: express never waits, the lane absorbs the spacing.
/// </summary>
public sealed class PoliteGate(AdaptiveDelay delay, TimeProvider time)
{
    private readonly SemaphoreSlim _turnstile = new(1, 1);

    private readonly Lock _stamp = new();

    private long? _lastReleaseTimestamp;

    public async Task WaitTurnAsync(CancellationToken cancellationToken)
    {
        await _turnstile.WaitAsync(cancellationToken);
        try
        {
            long? last;
            lock (_stamp)
            {
                last = _lastReleaseTimestamp;
            }

            if (last is { } stamped)
            {
                var elapsed = time.GetElapsedTime(stamped);
                var remaining = delay.Current - elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, time, cancellationToken);
                }
            }

            RecordFetchNow();
        }
        finally
        {
            _turnstile.Release();
        }
    }

    /// <summary>An express visit reporting "the site just heard from us"
    /// without taking a turn. Never blocks.</summary>
    public void RecordFetchNow()
    {
        lock (_stamp)
        {
            _lastReleaseTimestamp = time.GetTimestamp();
        }
    }
}
