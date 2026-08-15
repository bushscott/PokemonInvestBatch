namespace PokemonInvestBatch.Application.Pokedex;

/// <summary>
/// Recognizes item-card titles — Energy, Fossil, Spirit Link, Poké Ball,
/// Poké Doll, Pokédex and similar non-Pokémon cards — that would otherwise
/// match a species name embedded in their title: "Clefairy Doll" contains
/// "Clefairy"; "Charizard Spirit Link" contains "Charizard". The species
/// matcher consults this before matching (ADR-0011, spec §4): a denylist hit
/// forces <c>NoSpecies</c> even when a species name appears in the title.
///
/// The list is a seed, not exhaustive. It grows via quarantine spot-checks —
/// when review of the quarantine queue turns up an item card that slipped
/// past it, the offending pattern is added here alongside a test for it.
/// </summary>
public static class ItemCardDenylist
{
    /// <summary>
    /// Normalized-title suffixes that mark an item card on their own,
    /// checked with <see cref="string.EndsWith(string, StringComparison)"/>.
    /// </summary>
    private static readonly string[] Suffixes = { " energy" };

    /// <summary>
    /// Normalized-title substrings that mark an item card wherever they
    /// appear, checked with
    /// <see cref="string.Contains(string, StringComparison)"/>.
    /// </summary>
    private static readonly string[] Substrings =
    {
        "spirit link",
        " doll",
        " fossil",
        "poke ball",
        "'s pokedex",
    };

    /// <summary>
    /// True when <paramref name="normalizedTitle"/> — the output of
    /// <see cref="TitleNormalizer.Normalize"/> — matches a known item-card
    /// pattern: the title is exactly "energy" or ends with " energy", or it
    /// contains one of <see cref="Substrings"/>.
    /// </summary>
    public static bool IsItemCard(string normalizedTitle)
    {
        if (string.IsNullOrEmpty(normalizedTitle))
        {
            return false;
        }

        if (string.Equals(normalizedTitle, "energy", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var suffix in Suffixes)
        {
            if (normalizedTitle.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var substring in Substrings)
        {
            if (normalizedTitle.Contains(substring, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
