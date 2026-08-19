using PokemonInvestBatch.Application.Enrichment;

namespace PokemonInvestBatch.Application.Tests.Enrichment;

public class SetMapperTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoAliases =
        new Dictionary<string, IReadOnlyList<string>>();

    private static TcgdexSet Set(string id, string name, string serie = "swsh") =>
        new()
        {
            Id = id,
            Name = name,
            SerieId = serie,
            SerieName = serie,
            ReleaseDate = new DateOnly(2021, 8, 27),
            OfficialCount = 100,
            TotalCount = 100,
        };

    [Theory]
    [InlineData("pokemon-japanese-eevee-heroes", SetPartition.Japanese)]
    [InlineData("pokemon-chinese-151-collect", SetPartition.Chinese)]
    [InlineData("pokemon-korean-scarlet-&-violet-151", SetPartition.Korean)]
    [InlineData("pokemon-1999-topps-movie", SetPartition.Topps)]
    [InlineData("pokemon-2000-topps-chrome", SetPartition.Topps)]
    [InlineData("pokemon-base-set", SetPartition.English)]
    [InlineData("pokemon-burger-king", SetPartition.English)]
    public void Slugs_partition_by_language_shelf(string slug, SetPartition partition)
    {
        Assert.Equal(partition, SetMapper.PartitionOf(slug));
    }

    [Fact]
    public void An_exact_name_match_maps()
    {
        var catalog = new TcgdexCatalog([Set("swsh7", "Evolving Skies")]);

        var map = SetMapper.Resolve(
            [("pokemon-evolving-skies", "Pokemon Evolving Skies")], catalog, NoAliases);

        var entry = map["pokemon-evolving-skies"];
        Assert.Equal(SetMapKind.Mapped, entry.Kind);
        Assert.Equal(["swsh7"], entry.TcgdexSetIds);
    }

    [Fact]
    public void A_korean_set_never_reaches_name_matching()
    {
        // "Pokemon Korean Scarlet & Violet 151" would happily alias onto
        // TCGdex's "151" and silently enrich Korean cards with English-set
        // data — wrong-but-plausible, the failure class this partition rule
        // exists to prevent. Excluded before any comparison runs.
        var catalog = new TcgdexCatalog([Set("sv03.5", "151", "sv")]);

        var map = SetMapper.Resolve(
            [("pokemon-korean-scarlet-&-violet-151", "Pokemon Korean Scarlet & Violet 151")],
            catalog,
            NoAliases);

        Assert.Equal(SetMapKind.Unmapped, map["pokemon-korean-scarlet-&-violet-151"].Kind);
    }

    [Fact]
    public void A_japanese_set_with_a_curated_alias_maps_without_name_matching()
    {
        // The en shelf holds its own '151' — a fuzzy or name-driven path
        // would land there. The ja join may only follow the curated alias
        // into the ja catalog.
        var english = new TcgdexCatalog([Set("sv03.5", "151", "sv")]);
        var japanese = new SetMapper.JapaneseShelf(
            new TcgdexCatalog([Set("SV2a", "ポケモンカード151", "sv-ja")]),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["pokemon-japanese-scarlet-&-violet-151"] = ["SV2a"],
            });

        var map = SetMapper.Resolve(
            [("pokemon-japanese-scarlet-&-violet-151", "Pokemon Japanese Scarlet & Violet 151")],
            english,
            NoAliases,
            japanese);

        var entry = map["pokemon-japanese-scarlet-&-violet-151"];
        Assert.Equal(SetPartition.Japanese, entry.Partition);
        Assert.Equal(SetMapKind.Mapped, entry.Kind);
        Assert.Equal(["SV2a"], entry.TcgdexSetIds);
    }

    [Fact]
    public void An_unaliased_japanese_set_stays_unmapped()
    {
        // The ja shelf being wired changes nothing for a set no human has
        // aliased: there is no name matching to fall back to, by design.
        var japanese = new SetMapper.JapaneseShelf(
            new TcgdexCatalog([Set("S6a", "イーブイヒーローズ", "swsh-ja")]),
            NoAliases);

        var map = SetMapper.Resolve(
            [("pokemon-japanese-eevee-heroes", "Pokemon Japanese Eevee Heroes")],
            new TcgdexCatalog([]),
            NoAliases,
            japanese);

        Assert.Equal(SetMapKind.Unmapped, map["pokemon-japanese-eevee-heroes"].Kind);
    }

    [Fact]
    public void A_japanese_alias_naming_a_missing_ja_set_refuses_loudly()
    {
        // Same posture as the English alias table: a silently dropped alias
        // would quietly unmap a curated set, so a dangling target throws.
        var japanese = new SetMapper.JapaneseShelf(
            new TcgdexCatalog([Set("SV2a", "ポケモンカード151", "sv-ja")]),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["pokemon-japanese-vstar-universe"] = ["S12a"],
            });

        Assert.Throws<InvalidOperationException>(() => _ = SetMapper.Resolve(
            [("pokemon-japanese-vstar-universe", "Pokemon Japanese Vstar Universe")],
            new TcgdexCatalog([]),
            NoAliases,
            japanese));
    }

    [Fact]
    public void Without_a_japanese_shelf_a_japanese_set_stays_unmapped()
    {
        // EnrichmentLane resolves without the ja shelf until the per-card
        // phase ships its own guard — Japanese stays honestly Unmapped there.
        var map = SetMapper.Resolve(
            [("pokemon-japanese-eevee-heroes", "Pokemon Japanese Eevee Heroes")],
            new TcgdexCatalog([]),
            NoAliases);

        Assert.Equal(SetMapKind.Unmapped, map["pokemon-japanese-eevee-heroes"].Kind);
    }

    [Fact]
    public void A_digital_pocket_set_is_never_a_candidate()
    {
        // TCG Pocket reuses appealing physical-sounding names; a physical
        // product must not map onto the digital catalog.
        var catalog = new TcgdexCatalog([Set("A3b", "Eevee Grove", "tcgp")]);

        var map = SetMapper.Resolve([("pokemon-eevee-grove", "Pokemon Eevee Grove")], catalog, NoAliases);

        Assert.Equal(SetMapKind.Unmapped, map["pokemon-eevee-grove"].Kind);
    }

    [Fact]
    public void The_promo_grab_bag_is_a_pool_not_a_set()
    {
        var catalog = new TcgdexCatalog([Set("swshp", "SWSH Black Star Promos")]);

        var map = SetMapper.Resolve([(SetMapper.PromoSlug, "Pokemon Promo")], catalog, NoAliases);

        Assert.Equal(SetMapKind.PromoPool, map[SetMapper.PromoSlug].Kind);
    }

    [Fact]
    public void An_alias_maps_where_names_cannot()
    {
        var catalog = new TcgdexCatalog([Set("sv03.5", "151", "sv")]);
        var aliases = new Dictionary<string, IReadOnlyList<string>>
        {
            ["pokemon-scarlet-&-violet-151"] = ["sv03.5"],
        };

        var map = SetMapper.Resolve(
            [("pokemon-scarlet-&-violet-151", "Pokemon Scarlet & Violet 151")], catalog, aliases);

        Assert.Equal(SetMapKind.Mapped, map["pokemon-scarlet-&-violet-151"].Kind);
        Assert.Equal(["sv03.5"], map["pokemon-scarlet-&-violet-151"].TcgdexSetIds);
    }

    [Fact]
    public void An_alias_to_a_set_the_mirror_lacks_is_a_loud_config_error()
    {
        var catalog = new TcgdexCatalog([Set("swsh7", "Evolving Skies")]);
        var aliases = new Dictionary<string, IReadOnlyList<string>>
        {
            ["pokemon-expedition"] = ["ecard1"],
        };

        Assert.Throws<InvalidOperationException>(() =>
            SetMapper.Resolve([("pokemon-expedition", "Pokemon Expedition")], catalog, aliases));
    }

    [Fact]
    public void A_catalog_name_collision_unmaps_rather_than_guessing()
    {
        var catalog = new TcgdexCatalog([Set("a", "Twin Names"), Set("b", "Twin Names")]);

        var map = SetMapper.Resolve([("pokemon-twin-names", "Pokemon Twin Names")], catalog, NoAliases);

        Assert.Equal(SetMapKind.Unmapped, map["pokemon-twin-names"].Kind);
    }

    [Fact]
    public void A_name_with_no_counterpart_is_honestly_unmapped()
    {
        var catalog = new TcgdexCatalog([Set("swsh7", "Evolving Skies")]);

        var map = SetMapper.Resolve(
            [("pokemon-world-championships-2024", "Pokemon World Championships 2024")], catalog, NoAliases);

        Assert.Equal(SetMapKind.Unmapped, map["pokemon-world-championships-2024"].Kind);
    }
}
