namespace PokemonInvestBatch.Application.Enrichment;

/// <summary>
/// Reduces a set's display name to the form the two catalogs are compared
/// in: "Pokemon Scarlet &amp; Violet" (PriceCharting) and "Scarlet &amp; Violet"
/// (TCGdex) both become "SCARLET AND VIOLET". Ampersand-vs-"and" and the
/// franchise prefix are the only vocabulary normalized — years, qualifiers,
/// and every distinguishing word survive, because this feeds an
/// exact-equality match and nothing fuzzier (ADR-0009: fuzzy set matching is
/// how Korean 151 silently becomes English 151).
/// </summary>
public static class SetNameNormalizer
{
    public static string Normalize(string name)
    {
        var folded = NameFold.Fold(name.Replace("&", " and ", StringComparison.Ordinal));
        const string prefix = "POKEMON ";
        return folded.StartsWith(prefix, StringComparison.Ordinal) ? folded[prefix.Length..] : folded;
    }
}
