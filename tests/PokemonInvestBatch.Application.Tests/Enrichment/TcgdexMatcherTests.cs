using PokemonInvestBatch.Application.Enrichment;

namespace PokemonInvestBatch.Application.Tests.Enrichment;

/// <summary>
/// Phase B verdicts, on the structures the research verified live: Evolving
/// Skies for the plain join, Celebrations for the name gate, galleries and
/// promo eras for prefix routing, and the promo grab-bag for honest
/// ambiguity. Guessing is never a verdict.
/// </summary>
public class TcgdexMatcherTests
{
    private static TcgdexCard Card(string setId, string localId, string name) =>
        new() { Id = $"{setId}-{localId}", LocalId = localId, Name = name };

    private static TcgdexSet Set(string id, string name, int official, params TcgdexCard[] cards) =>
        new()
        {
            Id = id,
            Name = name,
            SerieId = "swsh",
            SerieName = "Sword & Shield",
            ReleaseDate = new DateOnly(2021, 8, 27),
            OfficialCount = official,
            TotalCount = cards.Length,
            Cards = cards,
        };

    private static SetMapEntry Mapped(string slug, params string[] ids) =>
        new()
        {
            Slug = slug,
            Partition = SetPartition.English,
            Kind = SetMapKind.Mapped,
            TcgdexSetIds = ids,
        };

    private static readonly TcgdexSet EvolvingSkies = Set(
        "swsh7", "Evolving Skies", 203,
        Card("swsh7", "95", "Umbreon VMAX"),
        Card("swsh7", "215", "Umbreon VMAX"),
        Card("swsh7", "235", "Lightning Energy"));

    /// <summary>The ja join's fixtures: SV2a with values off the pinned ja
    /// mirror (2026-08-19), a Japanese-partition mapped entry, and the
    /// species guard built from real species_names rows.</summary>
    private static readonly TcgdexSet Pokemon151Ja = new()
    {
        Id = "SV2a",
        Name = "ポケモンカード151",
        SerieId = "sv",
        SerieName = "ポケモンカードゲーム スカーレット&バイオレット",
        ReleaseDate = new DateOnly(2023, 6, 16),
        OfficialCount = 165,
        TotalCount = 210,
        Cards =
        [
            new TcgdexCard { Id = "SV2a-025", LocalId = "025", Name = "ピカチュウ" },
            new TcgdexCard { Id = "SV2a-006", LocalId = "006", Name = "リザードンex" },
            new TcgdexCard { Id = "SV2a-159", LocalId = "159", Name = "ハイパーボール" },
        ],
    };

    private static SetMapEntry JapaneseMapped(string slug, params string[] ids) =>
        new()
        {
            Slug = slug,
            Partition = SetPartition.Japanese,
            Kind = SetMapKind.Mapped,
            TcgdexSetIds = ids,
        };

    private static TcgdexMatcher.JapaneseCardJoin JaJoin() => new(
        new TcgdexCatalog([Pokemon151Ja]),
        SpeciesAgreement.Build([(25, "ピカチュウ"), (6, "リザードン"), (1, "フシギダネ")]));

    [Fact]
    public void A_japanese_number_match_with_species_agreement_confirms()
    {
        var verdict = TcgdexMatcher.Match(
            "Pikachu #025",
            JapaneseMapped("pokemon-japanese-scarlet-&-violet-151", "SV2a"),
            new TcgdexCatalog([]),
            JaJoin(),
            new HashSet<int> { 25 });

        Assert.Equal(TcgdexMatchStatus.Confirmed, verdict.Status);
        Assert.Equal("025", verdict.CardNumber);
        Assert.Equal(165, verdict.SetOfficialSize);
        Assert.Equal("SV2a", verdict.TcgdexSetId);
        Assert.Equal("SV2a-025", verdict.TcgdexCardId);
        Assert.Equal("ピカチュウ", verdict.TcgdexName);
    }

    [Fact]
    public void A_japanese_number_match_with_disagreeing_species_is_a_name_mismatch()
    {
        // The wrong-set collision catch: the PC title says Pikachu, the
        // number lands on Charizard ex — species disagree, nothing written,
        // evidence recorded for review.
        var verdict = TcgdexMatcher.Match(
            "Pikachu #6",
            JapaneseMapped("pokemon-japanese-scarlet-&-violet-151", "SV2a"),
            new TcgdexCatalog([]),
            JaJoin(),
            new HashSet<int> { 25 });

        Assert.Equal(TcgdexMatchStatus.NameMismatch, verdict.Status);
        Assert.Null(verdict.CardNumber);
        Assert.Equal("SV2a-006", verdict.TcgdexCardId);
        Assert.Equal("リザードンex", verdict.TcgdexName);
    }

    [Fact]
    public void A_japanese_trainer_gets_the_no_guard_status_from_either_side()
    {
        // No species on the TCGdex side: the candidate is Ultra Ball.
        var trainerCandidate = TcgdexMatcher.Match(
            "Ultra Ball #159",
            JapaneseMapped("pokemon-japanese-scarlet-&-violet-151", "SV2a"),
            new TcgdexCatalog([]),
            JaJoin(),
            new HashSet<int> { 25 });
        Assert.Equal(TcgdexMatchStatus.NoSpeciesGuard, trainerCandidate.Status);
        Assert.Null(trainerCandidate.TcgdexCardId);

        // No species on the PC side: the card carries no species tag.
        var untaggedCard = TcgdexMatcher.Match(
            "Pikachu #025",
            JapaneseMapped("pokemon-japanese-scarlet-&-violet-151", "SV2a"),
            new TcgdexCatalog([]),
            JaJoin(),
            new HashSet<int>());
        Assert.Equal(TcgdexMatchStatus.NoSpeciesGuard, untaggedCard.Status);
    }

    [Fact]
    public void A_japanese_number_missing_from_the_mapped_set_is_number_not_found()
    {
        var verdict = TcgdexMatcher.Match(
            "Mew ex #205",
            JapaneseMapped("pokemon-japanese-scarlet-&-violet-151", "SV2a"),
            new TcgdexCatalog([]),
            JaJoin(),
            new HashSet<int> { 151 });

        Assert.Equal(TcgdexMatchStatus.NumberNotFound, verdict.Status);
    }

    [Fact]
    public void A_japanese_card_without_the_ja_join_wired_refuses_loudly()
    {
        // A Mapped Japanese entry reaching Match without the ja join is a
        // wiring bug, not a verdict — same loudness as a dangling alias.
        Assert.Throws<InvalidOperationException>(() => _ = TcgdexMatcher.Match(
            "Pikachu #025",
            JapaneseMapped("pokemon-japanese-scarlet-&-violet-151", "SV2a"),
            new TcgdexCatalog([])));
    }

    [Fact]
    public void A_number_hit_with_an_agreeing_name_confirms()
    {
        var verdict = TcgdexMatcher.Match(
            "Umbreon VMAX #215", Mapped("pokemon-evolving-skies", "swsh7"), new TcgdexCatalog([EvolvingSkies]));

        Assert.Equal(TcgdexMatchStatus.Confirmed, verdict.Status);
        Assert.Equal("215", verdict.CardNumber);
        Assert.Equal(203, verdict.SetOfficialSize);
        Assert.Equal("swsh7", verdict.TcgdexSetId);
        Assert.Equal("swsh7-215", verdict.TcgdexCardId);
        Assert.Equal("Umbreon VMAX", verdict.TcgdexName);
    }

    [Fact]
    public void Two_same_name_cards_resolve_by_number_alone()
    {
        // Evolving Skies holds two distinct Umbreon VMAX (95 and 215) — the
        // reason name alone can never be the join key.
        var catalog = new TcgdexCatalog([EvolvingSkies]);

        Assert.Equal(
            "swsh7-95",
            TcgdexMatcher.Match("Umbreon VMAX #95", Mapped("s", "swsh7"), catalog).TcgdexCardId);
        Assert.Equal(
            "swsh7-215",
            TcgdexMatcher.Match("Umbreon VMAX #215", Mapped("s", "swsh7"), catalog).TcgdexCardId);
    }

    [Fact]
    public void An_energy_synonym_still_confirms()
    {
        var verdict = TcgdexMatcher.Match(
            "Electric Energy #235", Mapped("s", "swsh7"), new TcgdexCatalog([EvolvingSkies]));

        Assert.Equal(TcgdexMatchStatus.Confirmed, verdict.Status);
        Assert.Equal("Lightning Energy", verdict.TcgdexName);
    }

    [Fact]
    public void Variant_products_inherit_the_same_card()
    {
        var baseSet = Set("base1", "Base Set", 102, Card("base1", "4", "Charizard"));
        var catalog = new TcgdexCatalog([baseSet]);

        var shadowless = TcgdexMatcher.Match("Charizard [Shadowless] #4", Mapped("s", "base1"), catalog);
        var firstEdition = TcgdexMatcher.Match("Charizard [1st Edition] #4", Mapped("s", "base1"), catalog);

        // Deliberately N:1 — number and set size are identical across print
        // variants, so both products confirm onto one TCGdex card.
        Assert.Equal(TcgdexMatchStatus.Confirmed, shadowless.Status);
        Assert.Equal(shadowless.TcgdexCardId, firstEdition.TcgdexCardId);
    }

    [Fact]
    public void The_name_gate_catches_celebrations_classic_collection()
    {
        // PC files the Classic Collection reprints under their original
        // numbers ("Charizard #4"); Celebrations' own #4 is Palkia and the
        // CC sibling numbers CC001–CC025. Probed live 2026-08-13.
        var celebrations = Set("cel25", "Celebrations", 25,
            Card("cel25", "1", "Ho-Oh"), Card("cel25", "4", "Palkia"));
        var classic = Set("cel25cc", "Celebrations Classic Collection", 25,
            Card("cel25cc", "CC002", "Charizard"));
        var catalog = new TcgdexCatalog([celebrations, classic]);

        var verdict = TcgdexMatcher.Match("Charizard #4", Mapped("s", "cel25"), catalog);

        Assert.Equal(TcgdexMatchStatus.NameMismatch, verdict.Status);
        Assert.Null(verdict.CardNumber);
        Assert.Null(verdict.SetOfficialSize);
        Assert.Equal("Palkia", verdict.TcgdexName);
    }

    [Fact]
    public void A_cc_prefixed_number_routes_to_the_classic_collection_sibling()
    {
        var celebrations = Set("cel25", "Celebrations", 25, Card("cel25", "4", "Palkia"));
        var classic = Set("cel25cc", "Celebrations Classic Collection", 25,
            Card("cel25cc", "CC002", "Charizard"));
        var catalog = new TcgdexCatalog([celebrations, classic]);

        var verdict = TcgdexMatcher.Match("Charizard #CC2", Mapped("s", "cel25"), catalog);

        Assert.Equal(TcgdexMatchStatus.Confirmed, verdict.Status);
        Assert.Equal("CC002", verdict.CardNumber);
        Assert.Equal("cel25cc", verdict.TcgdexSetId);
    }

    [Fact]
    public void A_tg_number_routes_to_the_trainer_gallery_sibling()
    {
        var brilliantStars = Set("swsh9", "Brilliant Stars", 172, Card("swsh9", "23", "Charizard V"));
        var gallery = Set("swsh9.5tg", "Brilliant Stars Trainer Gallery", 30,
            Card("swsh9.5tg", "TG23", "Umbreon VMAX"));
        var catalog = new TcgdexCatalog([brilliantStars, gallery]);

        var verdict = TcgdexMatcher.Match("Umbreon VMAX #TG23", Mapped("s", "swsh9"), catalog);

        Assert.Equal(TcgdexMatchStatus.Confirmed, verdict.Status);
        Assert.Equal("TG23", verdict.CardNumber);
        // The routed sibling's denominator, not the parent's.
        Assert.Equal(30, verdict.SetOfficialSize);
        Assert.Equal("swsh9.5tg", verdict.TcgdexSetId);
    }

    [Fact]
    public void Zero_padding_differences_still_meet()
    {
        var gallery = Set("swsh9.5tg", "Brilliant Stars Trainer Gallery", 30,
            Card("swsh9.5tg", "TG04", "Flareon"));
        var parent = Set("swsh9", "Brilliant Stars", 172);
        var catalog = new TcgdexCatalog([parent, gallery]);

        var verdict = TcgdexMatcher.Match("Flareon #TG4", Mapped("s", "swsh9"), catalog);

        Assert.Equal(TcgdexMatchStatus.Confirmed, verdict.Status);
        Assert.Equal("TG04", verdict.CardNumber);
    }

    [Fact]
    public void An_era_prefixed_promo_routes_by_prefix_from_the_grab_bag()
    {
        var promos = Set("swshp", "SWSH Black Star Promos", 307,
            Card("swshp", "SWSH262", "Charizard VSTAR"));
        var catalog = new TcgdexCatalog([promos]);
        var entry = new SetMapEntry
        {
            Slug = SetMapper.PromoSlug,
            Partition = SetPartition.English,
            Kind = SetMapKind.PromoPool,
        };

        var verdict = TcgdexMatcher.Match("Charizard VStar #SWSH262", entry, catalog);

        Assert.Equal(TcgdexMatchStatus.Confirmed, verdict.Status);
        Assert.Equal("SWSH262", verdict.CardNumber);
        Assert.Equal("swshp", verdict.TcgdexSetId);
    }

    [Fact]
    public void An_era_prefixed_promo_filed_inside_a_themed_set_routes_out()
    {
        // Celebrations holds "Lance's Charizard V #SWSH133"; the number
        // lives in the promo set, not in Celebrations.
        var celebrations = Set("cel25", "Celebrations", 25, Card("cel25", "4", "Palkia"));
        var promos = Set("swshp", "SWSH Black Star Promos", 307,
            Card("swshp", "SWSH133", "Lance's Charizard V"));
        var catalog = new TcgdexCatalog([celebrations, promos]);

        var verdict = TcgdexMatcher.Match("Lance's Charizard V #SWSH133", Mapped("s", "cel25"), catalog);

        Assert.Equal(TcgdexMatchStatus.Confirmed, verdict.Status);
        Assert.Equal("swshp", verdict.TcgdexSetId);
    }

    [Fact]
    public void A_bare_numbered_promo_colliding_across_eras_is_ambiguous()
    {
        var wizards = Set("basep", "Wizards Black Star Promos", 53, Card("basep", "44", "Charmander"));
        var nintendo = Set("np", "Nintendo Black Star Promos", 40, Card("np", "44", "Charmander"));
        var catalog = new TcgdexCatalog([wizards, nintendo]);
        var entry = new SetMapEntry
        {
            Slug = SetMapper.PromoSlug,
            Partition = SetPartition.English,
            Kind = SetMapKind.PromoPool,
        };

        var verdict = TcgdexMatcher.Match("Charmander #44", entry, catalog);

        // Same number, same name, two eras — leave unmatched rather than guess.
        Assert.Equal(TcgdexMatchStatus.Ambiguous, verdict.Status);
        Assert.Null(verdict.CardNumber);
    }

    [Fact]
    public void A_bare_numbered_promo_unique_across_the_pool_confirms()
    {
        var wizards = Set("basep", "Wizards Black Star Promos", 53, Card("basep", "44", "Charmander"));
        var svPromos = Set("svp", "SVP Black Star Promos", 225, Card("svp", "053", "Mew ex"));
        var catalog = new TcgdexCatalog([wizards, svPromos]);
        var entry = new SetMapEntry
        {
            Slug = SetMapper.PromoSlug,
            Partition = SetPartition.English,
            Kind = SetMapKind.PromoPool,
        };

        var verdict = TcgdexMatcher.Match("Mew ex #53", entry, catalog);

        Assert.Equal(TcgdexMatchStatus.Confirmed, verdict.Status);
        // TCGdex's own zero-padded spelling is what gets stored.
        Assert.Equal("053", verdict.CardNumber);
    }

    [Fact]
    public void A_zero_official_count_yields_no_denominator()
    {
        // mep publishes official: 0 (probed 2026-08-13). A denominator of
        // zero is a lie, not a size — the number is still real.
        var promos = Set("mep", "MEP Black Star Promos", 0, Card("mep", "ME01", "Mega Kangaskhan ex"));
        var catalog = new TcgdexCatalog([promos]);

        var verdict = TcgdexMatcher.Match("Mega Kangaskhan ex #ME01", Mapped("s", "mep"), catalog);

        Assert.Equal(TcgdexMatchStatus.Confirmed, verdict.Status);
        Assert.Equal("ME01", verdict.CardNumber);
        Assert.Null(verdict.SetOfficialSize);
    }

    [Fact]
    public void A_number_the_set_does_not_carry_is_not_found()
    {
        var verdict = TcgdexMatcher.Match(
            "Umbreon VMAX #999", Mapped("s", "swsh7"), new TcgdexCatalog([EvolvingSkies]));

        Assert.Equal(TcgdexMatchStatus.NumberNotFound, verdict.Status);
    }

    [Fact]
    public void No_number_means_nothing_to_join_on()
    {
        var verdict = TcgdexMatcher.Match(
            "Booster Box [1st Edition]", Mapped("s", "swsh7"), new TcgdexCatalog([EvolvingSkies]));

        Assert.Equal(TcgdexMatchStatus.NoNumber, verdict.Status);
    }

    [Fact]
    public void An_unmapped_set_is_an_honest_verdict_not_an_error()
    {
        var entry = new SetMapEntry
        {
            Slug = "pokemon-japanese-eevee-heroes",
            Partition = SetPartition.Japanese,
            Kind = SetMapKind.Unmapped,
        };

        var verdict = TcgdexMatcher.Match("Umbreon VMAX #95", entry, new TcgdexCatalog([EvolvingSkies]));

        Assert.Equal(TcgdexMatchStatus.UnmappedSet, verdict.Status);
    }

    [Fact]
    public void Verdict_equality_is_the_change_only_test()
    {
        var catalog = new TcgdexCatalog([EvolvingSkies]);
        var entry = Mapped("s", "swsh7");

        // Same inputs, same verdict value — a re-run writes nothing.
        Assert.Equal(
            TcgdexMatcher.Match("Umbreon VMAX #215", entry, catalog),
            TcgdexMatcher.Match("Umbreon VMAX #215", entry, catalog));
        Assert.NotEqual(
            TcgdexMatcher.Match("Umbreon VMAX #215", entry, catalog),
            TcgdexMatcher.Match("Umbreon VMAX #95", entry, catalog));
    }
}
