namespace PokemonInvestBatch.Application.Pokedex;

/// <summary>
/// The outcome of matching one card's title against the species catalog
/// (ADR-0011 item 3): the status the match produced, and the species ids it
/// found, in candidate-scan order (length-descending), not title order. Ids
/// are preserved on every status, including
/// <see cref="TagStatus.Quarantined"/> — quarantine keeps them for manual
/// review rather than discarding the evidence that caused it.
/// </summary>
public sealed record TagVerdict(TagStatus Status, IReadOnlyList<int> SpeciesIds);

/// <summary>
/// Matches a card's title against the species catalog by longest-name-first,
/// word-boundary-safe substring search (ADR-0011 item 3, spec §4). This is
/// the sole place that decides which species a title names — the daily
/// tagging sweep and the one-time 91k-card backfill both call
/// <see cref="Match"/> and write whatever verdict it returns.
/// </summary>
public static class SpeciesMatcher
{
    /// <summary>
    /// Matches <paramref name="rawTitle"/> against <paramref name="candidates"/>.
    ///
    /// Normalizes the title (<see cref="TitleNormalizer.Normalize"/>) first
    /// so punctuation, diacritics and casing differences between the title
    /// and the catalog can never hide a real match or manufacture a false
    /// one. If the normalized title is an item card
    /// (<see cref="ItemCardDenylist.IsItemCard"/>), returns
    /// <see cref="TagStatus.NoSpecies"/> immediately — the denylist beats
    /// any species match, deliberately, even when a species name is
    /// textually present ("Charizard Spirit Link" never tags as
    /// Charizard). Otherwise scans the candidates in the order given, each
    /// by repeated case-sensitive ordinal substring search, accepting a hit
    /// only when both neighbors are word boundaries and blanking each
    /// accepted span out of a scratch buffer so it cannot re-match a
    /// shorter candidate later in the scan — this is what stops "Porygon"
    /// from consuming a prefix of an already-matched "Porygon2" or
    /// "Porygon-Z". The distinct species ids found, in candidate-scan order
    /// (length-descending, not title order), decide the status: zero is
    /// <see cref="TagStatus.NoSpecies"/>, one to three is
    /// <see cref="TagStatus.Tagged"/>, four or more is
    /// <see cref="TagStatus.Quarantined"/> — guessing among four or more
    /// candidates is the one thing this match never does.
    /// </summary>
    /// <param name="candidates">(normalized name, species id), pre-sorted by
    /// name length descending by the caller — see
    /// <see cref="BuildCandidates"/>. This method trusts the given order and
    /// never re-sorts it.</param>
    public static TagVerdict Match(string rawTitle, IReadOnlyList<(string Name, int SpeciesId)> candidates)
    {
        var normalized = TitleNormalizer.Normalize(rawTitle);

        if (ItemCardDenylist.IsItemCard(normalized))
        {
            return new TagVerdict(TagStatus.NoSpecies, Array.Empty<int>());
        }

        // Accepted spans are overwritten with '\0' — a char no normalized
        // species name can ever contain — so consumed text stops matching
        // anything for the rest of the scan without shifting indices.
        var buffer = normalized.ToCharArray();
        var matchedIds = new List<int>();
        var seenSpeciesIds = new HashSet<int>();

        foreach (var (name, speciesId) in candidates)
        {
            if (name.Length == 0)
            {
                continue;
            }

            var searchFrom = 0;
            while (searchFrom <= buffer.Length - name.Length)
            {
                var relativeIndex = buffer.AsSpan(searchFrom).IndexOf(name.AsSpan(), StringComparison.Ordinal);
                if (relativeIndex < 0)
                {
                    break;
                }

                var index = searchFrom + relativeIndex;
                if (HasWordBoundaries(buffer, index, name.Length))
                {
                    Array.Fill(buffer, '\0', index, name.Length);

                    if (seenSpeciesIds.Add(speciesId))
                    {
                        matchedIds.Add(speciesId);
                    }
                }

                // Advance past this occurrence whether or not it was
                // accepted, so a boundary-rejected hit (e.g. "mew" inside
                // "mewtwo", blocked by the trailing 't') doesn't re-find the
                // same index forever.
                searchFrom = index + 1;
            }
        }

        var status = matchedIds.Count switch
        {
            0 => TagStatus.NoSpecies,
            <= 3 => TagStatus.Tagged,
            _ => TagStatus.Quarantined,
        };

        return new TagVerdict(status, matchedIds);
    }

    /// <summary>
    /// Builds the candidate list <see cref="Match"/> scans: each species'
    /// English name normalized the same way titles are
    /// (<see cref="TitleNormalizer.Normalize"/>), sorted by normalized-name
    /// length descending so compound and multi-word names claim their text
    /// before a shorter name embedded in them can. Ties break ordinally —
    /// load-bearing for "mime jr." vs "mr. mime" (both 8 characters
    /// normalized). Not because either order could make one match as the
    /// other: equal-length strings can never nest as substrings of each
    /// other, so no tie-break order changes any match verdict here. It is
    /// what makes the candidate order — and therefore
    /// <see cref="TagVerdict.SpeciesIds"/>' order, for a title that names
    /// more than one equal-length candidate — deterministic rather than an
    /// accident of the caller's enumeration order.
    /// </summary>
    public static IReadOnlyList<(string Name, int SpeciesId)> BuildCandidates(IEnumerable<(int Id, string EnglishName)> species)
        => species
            .Select(s => (Name: TitleNormalizer.Normalize(s.EnglishName), SpeciesId: s.Id))
            .OrderByDescending(candidate => candidate.Name.Length)
            .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// True when the <paramref name="length"/>-char span starting at
    /// <paramref name="start"/> in <paramref name="buffer"/> is bounded on
    /// both sides by a word boundary: the buffer edge, or a character that
    /// is neither a letter/digit nor a gender glyph. ♀ (U+2640) and ♂
    /// (U+2642) count as name characters, not boundaries, so "nidoran"
    /// alone never boundary-matches inside "nidoran♀".
    /// </summary>
    private static bool HasWordBoundaries(char[] buffer, int start, int length)
    {
        var leftIsBoundary = start == 0 || IsBoundaryChar(buffer[start - 1]);
        var end = start + length;
        var rightIsBoundary = end == buffer.Length || IsBoundaryChar(buffer[end]);
        return leftIsBoundary && rightIsBoundary;
    }

    private static bool IsBoundaryChar(char c) => !char.IsLetterOrDigit(c) && c is not ('♀' or '♂');
}
