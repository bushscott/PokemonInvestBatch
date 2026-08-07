using PokemonInvestBatch.Application.Alerting;

namespace PokemonInvestBatch.TestSupport;

/// <summary>Records what the code tried to tell a human, so tests can assert
/// on the alarm as well as on the data.</summary>
public sealed class RecordingAlerter : IAlerter
{
    public List<(string Subject, string Body)> Raised { get; } = [];

    public Task RaiseAsync(string subject, string body, CancellationToken ct)
    {
        Raised.Add((subject, body));
        return Task.CompletedTask;
    }
}
