namespace PokemonInvestBatch.Domain.Parsing;

/// <summary>Everything a card detail page yields. Grows one slice at a time.</summary>
public sealed record CardDetailPage
{
    /// <summary>Graded census, absent on cards with no population report.</summary>
    public PopulationReport? Population { get; init; }
}
