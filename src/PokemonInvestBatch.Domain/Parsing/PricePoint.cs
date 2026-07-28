namespace PokemonInvestBatch.Domain.Parsing;

/// <summary>One month's average price for a tier, in cents (the site's own unit).</summary>
public readonly record struct PricePoint(DateOnly Month, int PriceCents);
