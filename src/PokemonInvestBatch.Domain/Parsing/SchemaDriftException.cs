namespace PokemonInvestBatch.Domain.Parsing;

/// <summary>
/// Thrown when a scraped page's structure differs from every known fingerprint —
/// an unknown key, series, tier class, or marketplace prefix. Drift must
/// fail loudly before any fact is written; silently skipping unknown data
/// is how a source change silently corrupts weeks of the catalog.
/// </summary>
public sealed class SchemaDriftException(string message, Exception? cause = null)
    : Exception(message, cause);
