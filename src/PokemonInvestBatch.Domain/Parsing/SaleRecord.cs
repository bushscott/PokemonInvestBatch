namespace PokemonInvestBatch.Domain.Parsing;

/// <summary>
/// One completed sale. <c>(Source, SourceId)</c> is the natural dedup key —
/// verified stable across fetches for all five marketplaces.
/// </summary>
public sealed record SaleRecord
{
    /// <summary>Marketplace prefix of the row id: ebay, tcgplayer, goldin, heritage, pwcc.</summary>
    public required string Source { get; init; }

    /// <summary>Marketplace-native id, HTML-entity decoded.</summary>
    public required string SourceId { get; init; }

    public required DateOnly SoldOn { get; init; }

    /// <summary>Grade bucket label exactly as the page names it (e.g. "PSA 10", "Grade 9.5").</summary>
    public required string GradeTier { get; init; }

    public required int PriceCents { get; init; }

    /// <summary>Original listing price when shown; most rows have none.</summary>
    public int? ListedPriceCents { get; init; }

    /// <summary>Raw listing title. Third-party text: store raw, encode on output.</summary>
    public required string Title { get; init; }
}
