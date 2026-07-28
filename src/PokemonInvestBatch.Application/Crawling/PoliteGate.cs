namespace PokemonInvestBatch.Application.Crawling;

/// <summary>
/// The single shared gate in front of pricecharting.com: every lane waits its
/// turn here, so the politeness budget is global no matter how many lanes run.
/// Image CDN fetches do not pass through this gate (different host).
/// </summary>
public sealed class PoliteGate(AdaptiveDelay delay, TimeProvider time)
{
    private readonly SemaphoreSlim _turnstile = new(1, 1);

    private long? _lastReleaseTimestamp;

    public async Task WaitTurnAsync(CancellationToken cancellationToken)
    {
        await _turnstile.WaitAsync(cancellationToken);
        try
        {
            if (_lastReleaseTimestamp is { } last)
            {
                var elapsed = time.GetElapsedTime(last);
                var remaining = delay.Current - elapsed;
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, time, cancellationToken);
                }
            }

            _lastReleaseTimestamp = time.GetTimestamp();
        }
        finally
        {
            _turnstile.Release();
        }
    }
}
