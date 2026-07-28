namespace PokemonInvestBatch.Application.Alerting;

/// <summary>Fail loudly: something needs the operator's eyes.</summary>
public interface IAlerter
{
    Task RaiseAsync(string subject, string body, CancellationToken cancellationToken);
}
