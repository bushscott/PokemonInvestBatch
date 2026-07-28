namespace PokemonInvestBatch.Application.Crawling;

/// <summary>
/// The single shared gate in front of pricecharting.com: every lane waits its
/// turn here, so the politeness budget is global no matter how many lanes run.
/// Image CDN fetches do not pass through this gate (different host).
/// </summary>
public sealed class PoliteGate(AdaptiveDelay delay, TimeProvider time)
{
    public Task WaitTurnAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
}
