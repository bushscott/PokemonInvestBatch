namespace PokemonInvestBatch.Application.Enrichment;

/// <summary>
/// The cross-script guard for the Japanese card join (ADR-0012): which
/// species a TCGdex ja card name actually names, derived through the
/// imported Japanese <c>species_names</c> (languages <c>ja</c> and
/// <c>ja-hrkt</c>). <see cref="CardNameAgreement"/> must never see Japanese
/// text — <see cref="NameFold"/> folds it to nothing, so two Japanese names
/// would compare equal-as-empty and confirm anything — and this class is
/// the replacement: a card's tagged species (from its English PriceCharting
/// title, ADR-0011) must be among the species its TCGdex ja name derives,
/// or the number match is refused.
///
/// The scan mirrors <see cref="Pokedex.SpeciesMatcher"/>: longest name
/// first, accepted spans consumed so ポリゴン can never re-read the text
/// ポリゴン2 already claimed. One deliberate difference: no word-boundary
/// rule — Japanese card names abut their Latin suffixes with no separator
/// (ピカチュウex, メガリザードンYex), so a letter-boundary check would
/// reject exactly the names this guard exists to read. Comparison is
/// ordinal and unnormalized: both sides are machine-imported strings, not
/// operator input.
/// </summary>
public sealed class SpeciesAgreement
{
    private readonly IReadOnlyList<(string Name, IReadOnlyList<int> SpeciesIds)> _candidates;

    private SpeciesAgreement(IReadOnlyList<(string Name, IReadOnlyList<int> SpeciesIds)> candidates)
    {
        _candidates = candidates;
    }

    /// <summary>Builds the guard from (species id, Japanese name) rows.
    /// Duplicate spellings collapse (ja and ja-hrkt usually agree); a
    /// spelling shared by more than one species keeps every id, so a
    /// derivation never silently drops a claimant.</summary>
    public static SpeciesAgreement Build(IEnumerable<(int SpeciesId, string Name)> japaneseNames)
    {
        var byName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var (speciesId, name) in japaneseNames)
        {
            if (name.Length == 0)
            {
                continue;
            }

            if (!byName.TryGetValue(name, out var ids))
            {
                ids = [];
                byName[name] = ids;
            }

            if (!ids.Contains(speciesId))
            {
                ids.Add(speciesId);
            }
        }

        return new SpeciesAgreement(byName
            .Select(pair => (pair.Key, (IReadOnlyList<int>)pair.Value))
            .OrderByDescending(candidate => candidate.Key.Length)
            .ThenBy(candidate => candidate.Key, StringComparer.Ordinal)
            .ToList());
    }

    /// <summary>Every species the given TCGdex ja card name names. Empty
    /// means the guard has nothing to vouch with — a trainer, an item, an
    /// energy, or a species this Pokédex does not know — and the caller
    /// must treat that as "no guard", never as agreement.</summary>
    public IReadOnlySet<int> SpeciesNamed(string tcgdexJaCardName)
    {
        // Accepted spans are overwritten with '\0' — a char no species name
        // can contain — so consumed text stops matching anything for the
        // rest of the scan without shifting indices (SpeciesMatcher's
        // mechanic, reused).
        var buffer = tcgdexJaCardName.ToCharArray();
        var named = new HashSet<int>();

        foreach (var (name, speciesIds) in _candidates)
        {
            var searchFrom = 0;
            while (searchFrom <= buffer.Length - name.Length)
            {
                var relativeIndex = buffer.AsSpan(searchFrom).IndexOf(name.AsSpan(), StringComparison.Ordinal);
                if (relativeIndex < 0)
                {
                    break;
                }

                var index = searchFrom + relativeIndex;
                Array.Fill(buffer, '\0', index, name.Length);
                foreach (var speciesId in speciesIds)
                {
                    named.Add(speciesId);
                }

                searchFrom = index + name.Length;
            }
        }

        return named;
    }
}
