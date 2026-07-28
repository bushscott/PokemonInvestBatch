namespace PokemonInvestBatch.Application.Alerting;

/// <summary>
/// One email per incident, not one per failed page: a keyed cooldown so a
/// site change that breaks thousands of pages raises a single summary.
/// </summary>
public sealed class IncidentThrottle(TimeSpan window)
{
    private readonly Dictionary<string, DateTimeOffset> _lastAlerted = [];

    private readonly Lock _lock = new();

    public bool ShouldAlert(string incidentKey, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_lastAlerted.TryGetValue(incidentKey, out var last) && now - last < window)
            {
                return false;
            }

            _lastAlerted[incidentKey] = now;
            return true;
        }
    }
}
