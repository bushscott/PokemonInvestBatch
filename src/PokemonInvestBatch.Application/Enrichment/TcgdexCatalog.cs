namespace PokemonInvestBatch.Application.Enrichment;

/// <summary>One card as the TCGdex catalog knows it.</summary>
public sealed record TcgdexCard
{
    /// <summary>Global id, e.g. "swsh7-215".</summary>
    public required string Id { get; init; }

    /// <summary>The collector number as printed, e.g. "215", "TG23", "053".</summary>
    public required string LocalId { get; init; }

    public required string Name { get; init; }
}

/// <summary>One set as the TCGdex catalog knows it.</summary>
public sealed record TcgdexSet
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>The serie the set belongs to ("swsh", "sv", "tcgp"…).</summary>
    public required string SerieId { get; init; }

    /// <summary>The serie's display name ("Sword &amp; Shield"), distinct
    /// from <see cref="SerieId"/> ("swsh") — the Pokédex phase's set-details
    /// sweep (ADR-0011) stores this verbatim in <c>set_details.series</c>
    /// and looks it up against the curated series→era file.</summary>
    public required string SerieName { get; init; }

    /// <summary>The set's release date, as TCGdex publishes it —
    /// <c>set_details.released_on</c> (ADR-0011).</summary>
    public required DateOnly ReleaseDate { get; init; }

    /// <summary>cardCount.official — the printed set size, the denominator in
    /// "215/203". Secret cards are numbered past it. Zero means TCGdex has no
    /// denominator for this set (new-era promo sets), not a size of zero.
    /// The other cardCount keys (holo/reverse/firstEd) are NOT modeled: they
    /// are demonstrably incoherent at the source (base1 declares firstEd 104
    /// against total 102, probed 2026-08-13).</summary>
    public required int OfficialCount { get; init; }

    public required int TotalCount { get; init; }

    public IReadOnlyList<TcgdexCard> Cards { get; init; } = [];

    /// <summary>Serie "tcgp" is TCG Pocket — the digital game. Its set names
    /// ("Genetic Apex", "Eevee Grove") must never be candidates for physical
    /// products, or a name coincidence would silently enrich a physical card
    /// with digital-catalog numbers.</summary>
    public bool IsPhysical => !string.Equals(SerieId, "tcgp", StringComparison.Ordinal);
}

/// <summary>
/// The TCGdex English catalog, indexed for the join: sets by id, physical
/// sets by normalized name, and the structural relatives the routing rules
/// need — per-era promo sets and the gallery/vault/classic siblings that
/// PriceCharting folds into their parent set but TCGdex splits out.
/// Everything here is derived from set names, so a future "X Trainer
/// Gallery" routes with no code change.
/// </summary>
public sealed class TcgdexCatalog
{
    private readonly Dictionary<string, TcgdexSet> byId;
    private readonly Dictionary<string, List<TcgdexSet>> physicalByNormalizedName;

    public TcgdexCatalog(IEnumerable<TcgdexSet> sets)
    {
        byId = sets.ToDictionary(s => s.Id, StringComparer.Ordinal);
        PhysicalSets = byId.Values.Where(s => s.IsPhysical).ToList();
        physicalByNormalizedName = PhysicalSets
            .GroupBy(s => SetNameNormalizer.Normalize(s.Name), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        PromoSets = PhysicalSets
            .Where(s => NameFold.Fold(s.Name).Contains("PROMO", StringComparison.Ordinal))
            .ToList();
    }

    public IReadOnlyList<TcgdexSet> PhysicalSets { get; }

    /// <summary>The per-era promo sets PriceCharting's one "Pokemon Promo"
    /// grab-bag fans out to (Wizards/Nintendo/DP/HGSS/BW/XY/SM/SWSH/SVP/MEP
    /// Black Star Promos, and the small promotional sets).</summary>
    public IReadOnlyList<TcgdexSet> PromoSets { get; }

    public TcgdexSet? ById(string id) => byId.GetValueOrDefault(id);

    /// <summary>Physical sets whose normalized name equals the given
    /// normalized name. More than one is a catalog collision the set mapper
    /// refuses to map through (measured zero across 2026-08-13's catalog).</summary>
    public IReadOnlyList<TcgdexSet> ByNormalizedName(string normalizedName) =>
        physicalByNormalizedName.GetValueOrDefault(normalizedName) ?? [];

    /// <summary>"SWSH262" → "SWSH Black Star Promos". Null when no promo set
    /// carries the era prefix — bare or unknown prefixes never guess.</summary>
    public TcgdexSet? PromoSetForPrefix(string alphaPrefix) =>
        alphaPrefix.Length == 0
            ? null
            : PromoSets.SingleOrDefaultSafe(s =>
                NameFold.Fold(s.Name) == $"{alphaPrefix} BLACK STAR PROMOS");

    /// <summary>"Brilliant Stars" → "Brilliant Stars Trainer Gallery" (TG numbers).</summary>
    public TcgdexSet? TrainerGalleryOf(TcgdexSet parent) => RelativeOf(parent, "TRAINER GALLERY");

    /// <summary>"Crown Zenith" → "Crown Zenith Galarian Gallery" (GG numbers).</summary>
    public TcgdexSet? GalarianGalleryOf(TcgdexSet parent) => RelativeOf(parent, "GALARIAN GALLERY");

    /// <summary>"Hidden Fates" → "Hidden Fates Shiny Vault" (SV numbers).</summary>
    public TcgdexSet? ShinyVaultOf(TcgdexSet parent) => RelativeOf(parent, "SHINY VAULT");

    /// <summary>"Celebrations" → "Celebrations Classic Collection" (CC numbers).</summary>
    public TcgdexSet? ClassicCollectionOf(TcgdexSet parent) => RelativeOf(parent, "CLASSIC COLLECTION");

    /// <summary>The standalone "Radiant Collection" set (RC numbers, folded by
    /// PriceCharting into Legendary Treasures).</summary>
    public IReadOnlyList<TcgdexSet> RadiantCollections =>
        ByNormalizedName(SetNameNormalizer.Normalize("Radiant Collection"));

    private TcgdexSet? RelativeOf(TcgdexSet parent, string foldedSuffix)
    {
        var wanted = $"{NameFold.Fold(parent.Name)} {foldedSuffix}";
        return PhysicalSets.SingleOrDefaultSafe(s => NameFold.Fold(s.Name) == wanted);
    }
}

internal static class TcgdexCatalogExtensions
{
    /// <summary>Single match or null — never a guess between two.</summary>
    public static TcgdexSet? SingleOrDefaultSafe(
        this IEnumerable<TcgdexSet> sets, Func<TcgdexSet, bool> predicate)
    {
        TcgdexSet? found = null;
        foreach (var set in sets.Where(predicate))
        {
            if (found is not null)
            {
                return null;
            }

            found = set;
        }

        return found;
    }
}
