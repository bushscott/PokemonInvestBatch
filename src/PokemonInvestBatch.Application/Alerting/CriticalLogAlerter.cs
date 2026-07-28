using Microsoft.Extensions.Logging;

namespace PokemonInvestBatch.Application.Alerting;

/// <summary>
/// Alert decisions live in New Relic; the app emits signals. A raise becomes
/// a Critical structured log, which journald forwarding lands in NR Logs
/// where alert conditions (and humans) can find it.
/// </summary>
public sealed class CriticalLogAlerter(ILogger<CriticalLogAlerter> logger) : IAlerter
{
    public Task RaiseAsync(string subject, string body, CancellationToken cancellationToken)
    {
        logger.LogCritical("ALERT: {Subject} — {Body}", subject, body);
        return Task.CompletedTask;
    }
}
