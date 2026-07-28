namespace PokemonInvestBatch.Domain.Parsing;

/// <summary>Everything a card detail page yields. Grows one slice at a time.</summary>
public sealed record CardDetailPage
{
    /// <summary>Monthly price history per tier; every card page carries all six series.</summary>
    public required IReadOnlyDictionary<PriceTier, IReadOnlyList<PricePoint>> Chart { get; init; }

    /// <summary>Graded census, absent on cards with no population report.</summary>
    public PopulationReport? Population { get; init; }

    /// <summary>Completed sales across all marketplaces and grade buckets.</summary>
    public required IReadOnlyList<SaleRecord> Sales { get; init; }

    /// <summary>CDN hash segment of the product image; the fetch-once key. Null when the card has no photo.</summary>
    public string? ImageHash { get; init; }
}
