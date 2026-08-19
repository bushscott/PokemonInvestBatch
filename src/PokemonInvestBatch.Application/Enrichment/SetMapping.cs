namespace PokemonInvestBatch.Application.Enrichment;

/// <summary>Which language shelf a PriceCharting set sits on, decided by its
/// slug alone. Everything except English is excluded from name matching
/// before any comparison runs — "Pokemon Korean Scarlet &amp; Violet 151"
/// must never meet TCGdex's "151", because TCGdex serves non-English sets
/// under other locales with non-Latin names and a near-miss here writes
/// wrong-but-plausible data (the exact failure this project's rules exist
/// to prevent).</summary>
public enum SetPartition : short
{
    English = 1,
    Japanese = 2,
    Chinese = 3,
    Korean = 4,
    Topps = 5,
}

/// <summary>How a PriceCharting set participates in the TCGdex join.</summary>
public enum SetMapKind : short
{
    /// <summary>Joined to one or more specific TCGdex sets (trainer kits are
    /// one PC set over two TCGdex half-deck sets).</summary>
    Mapped = 1,

    /// <summary>The one "Pokemon Promo" grab-bag: no single TCGdex set —
    /// cards route to per-era promo sets by their number prefix.</summary>
    PromoPool = 2,

    /// <summary>No TCGdex counterpart. Non-English partitions, and English
    /// products TCGdex does not carry (World Championships decks,
    /// merchandise lines). Every card here gets an honest UnmappedSet
    /// verdict, never a forced one.</summary>
    Unmapped = 3,
}

/// <summary>One PriceCharting set's place in the join.</summary>
public sealed record SetMapEntry
{
    public required string Slug { get; init; }

    public required SetPartition Partition { get; init; }

    public required SetMapKind Kind { get; init; }

    public IReadOnlyList<string> TcgdexSetIds { get; init; } = [];
}

/// <summary>
/// Phase A of the join: decide, per PriceCharting set, which TCGdex set(s)
/// its cards look in. Partition first (non-English never matches), then the
/// hand-curated alias table, then exact normalized-name equality — in that
/// order, and nothing fuzzier. Measured 2026-08-13: exact equality alone
/// maps 138 of 222 English sets (36,288 of 42,855 English cards) with zero
/// collisions either direction.
/// </summary>
public static class SetMapper
{
    /// <summary>The grab-bag slug that fans out to per-era promo sets.</summary>
    public const string PromoSlug = "pokemon-promo";

    /// <summary>The Japanese shelf's join inputs: its own locale's catalog
    /// and its own hand-curated alias table — the ONLY path a Japanese set
    /// may map through (ADR-0012). Japanese-script names fold to nothing in
    /// <see cref="NameFold"/>, so name matching is structurally impossible
    /// here, not merely switched off: a branch that consulted it could only
    /// ever produce wrong-but-plausible data. A caller that does not carry
    /// the shelf (the per-card enrichment, until its own guard ships) passes
    /// none, and Japanese stays honestly Unmapped as before.</summary>
    public sealed record JapaneseShelf(
        TcgdexCatalog Catalog,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Aliases);

    public static SetPartition PartitionOf(string slug)
    {
        if (slug.StartsWith("pokemon-japanese", StringComparison.Ordinal))
        {
            return SetPartition.Japanese;
        }

        if (slug.StartsWith("pokemon-chinese", StringComparison.Ordinal))
        {
            return SetPartition.Chinese;
        }

        if (slug.StartsWith("pokemon-korean", StringComparison.Ordinal))
        {
            return SetPartition.Korean;
        }

        if (slug.Contains("topps", StringComparison.Ordinal))
        {
            return SetPartition.Topps;
        }

        return SetPartition.English;
    }

    /// <summary>
    /// Resolve every PriceCharting set. Alias targets that name a TCGdex set
    /// the catalog does not contain are a configuration error and throw —
    /// a silently dropped alias would quietly unmap a curated set.
    /// </summary>
    public static IReadOnlyDictionary<string, SetMapEntry> Resolve(
        IEnumerable<(string Slug, string Name)> priceChartingSets,
        TcgdexCatalog catalog,
        IReadOnlyDictionary<string, IReadOnlyList<string>> aliases,
        JapaneseShelf? japanese = null)
    {
        var map = new Dictionary<string, SetMapEntry>(StringComparer.Ordinal);
        foreach (var (slug, name) in priceChartingSets)
        {
            map[slug] = ResolveOne(slug, name, catalog, aliases, japanese);
        }

        return map;
    }

    private static SetMapEntry ResolveOne(
        string slug,
        string name,
        TcgdexCatalog catalog,
        IReadOnlyDictionary<string, IReadOnlyList<string>> aliases,
        JapaneseShelf? japanese)
    {
        var partition = PartitionOf(slug);
        if (partition == SetPartition.Japanese && japanese is not null)
        {
            // Alias-only, into the ja catalog only — a hit or an honest
            // miss, with the same loud dangling-target check as the English
            // alias table and never a fallback to name matching.
            if (japanese.Aliases.TryGetValue(slug, out var jaTargets))
            {
                foreach (var target in jaTargets)
                {
                    if (japanese.Catalog.ById(target) is not { IsPhysical: true })
                    {
                        throw new InvalidOperationException(
                            $"Japanese set alias '{slug}' names TCGdex ja set '{target}', which the ja mirror " +
                            "does not contain (or is a digital set). Fix the alias file or refresh the mirror.");
                    }
                }

                return new SetMapEntry
                {
                    Slug = slug,
                    Partition = partition,
                    Kind = SetMapKind.Mapped,
                    TcgdexSetIds = jaTargets,
                };
            }

            return new SetMapEntry { Slug = slug, Partition = partition, Kind = SetMapKind.Unmapped };
        }

        if (partition != SetPartition.English)
        {
            return new SetMapEntry { Slug = slug, Partition = partition, Kind = SetMapKind.Unmapped };
        }

        if (string.Equals(slug, PromoSlug, StringComparison.Ordinal))
        {
            return new SetMapEntry { Slug = slug, Partition = partition, Kind = SetMapKind.PromoPool };
        }

        if (aliases.TryGetValue(slug, out var targets))
        {
            foreach (var target in targets)
            {
                if (catalog.ById(target) is not { IsPhysical: true })
                {
                    throw new InvalidOperationException(
                        $"Set alias '{slug}' names TCGdex set '{target}', which the mirror does not " +
                        "contain (or is a digital set). Fix the alias file or refresh the mirror.");
                }
            }

            return new SetMapEntry
            {
                Slug = slug,
                Partition = partition,
                Kind = SetMapKind.Mapped,
                TcgdexSetIds = targets,
            };
        }

        var candidates = catalog.ByNormalizedName(SetNameNormalizer.Normalize(name));
        if (candidates.Count == 1)
        {
            return new SetMapEntry
            {
                Slug = slug,
                Partition = partition,
                Kind = SetMapKind.Mapped,
                TcgdexSetIds = [candidates[0].Id],
            };
        }

        // Zero candidates is an honest miss; two or more is a catalog
        // collision that only a curated alias may resolve. Either way,
        // unmapped — never a guess.
        return new SetMapEntry { Slug = slug, Partition = partition, Kind = SetMapKind.Unmapped };
    }
}
