namespace PokemonInvestBatch.Domain.Parsing;

/// <summary>Everything a card detail page yields. Grows one slice at a time.</summary>
public sealed record CardDetailPage
{
    /// <summary>Monthly price history per tier; every card page carries all six series.</summary>
    public required IReadOnlyDictionary<PriceTier, IReadOnlyList<PricePoint>> Chart { get; init; }

    /// <summary>Graded census, absent on cards with no population report.</summary>
    public PopulationReport? Population { get; init; }
}
