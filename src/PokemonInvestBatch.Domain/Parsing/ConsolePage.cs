namespace PokemonInvestBatch.Domain.Parsing;

/// <summary>One cursor page of a set's product listing. Enumeration only — no prices by design.</summary>
public sealed record ConsolePage
{
    public required IReadOnlyList<ProductListing> Products { get; init; }

    /// <summary>Hidden fields of the site's own "more results" POST form; null on the last page.</summary>
    public IReadOnlyDictionary<string, string>? NextPageForm { get; init; }
}

/// <summary>A card as listed on a set page.</summary>
public sealed record ProductListing
{
    public required long ProductId { get; init; }

    /// <summary>Detail page path, e.g. "/game/pokemon-base-set/charizard-4".</summary>
    public required string Url { get; init; }

    public required string Name { get; init; }
}

/// <summary>A card set as listed on the category page.</summary>
public sealed record SetListing
{
    /// <summary>Slug after "/console/", entity-decoded — the blacklist key.</summary>
    public required string Slug { get; init; }

    public required string Name { get; init; }
}
