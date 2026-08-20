using PokemonInvestBatch.Application.Enrichment;

namespace PokemonInvestBatch.Application.Tests.Enrichment;

/// <summary>
/// The cross-script guard for the Japanese card join: which species a TCGdex
/// ja card name actually names, derived through the already-imported
/// Japanese species names. Names verified against the pinned ja mirror and
/// species_names fixtures (2026-08-19), not invented.
/// </summary>
public class SpeciesAgreementTests
{
    private static SpeciesAgreement Guard(params (int SpeciesId, string Name)[] names) =>
        SpeciesAgreement.Build(names);

    [Fact]
    public void A_possessive_card_name_still_names_its_species()
    {
        // エリカのモンジャラ = "Erika's Tangela" — the exact card the join
        // was proven on live (MC-743, 2026-08-06).
        var guard = Guard((114, "モンジャラ"));

        Assert.Equal([114], guard.SpeciesNamed("エリカのモンジャラ"));
    }

    [Fact]
    public void A_kana_name_with_a_latin_suffix_still_names_its_species()
    {
        // ピカチュウex — kana abuts the Latin suffix with no separator, so
        // the Pokédex matcher's letter-boundary rule would wrongly reject
        // it; this guard deliberately has no boundary rule.
        var guard = Guard((25, "ピカチュウ"));

        Assert.Equal([25], guard.SpeciesNamed("ピカチュウex"));
        Assert.Equal([6], Guard((6, "リザードン")).SpeciesNamed("メガリザードンYex"));
    }

    [Fact]
    public void The_longer_species_name_claims_its_text_first()
    {
        // ポリゴン2 contains ポリゴン; longest-first with consume-the-span
        // means Porygon2's card never also reads as Porygon.
        var guard = Guard((137, "ポリゴン"), (233, "ポリゴン2"));

        Assert.Equal([233], guard.SpeciesNamed("ポリゴン2"));
        Assert.Equal([137], guard.SpeciesNamed("ポリゴン"));
    }

    [Fact]
    public void A_regional_prefix_does_not_hide_the_species()
    {
        // アローラロコン = "Alolan Vulpix" — the regional form is a prefix
        // on the same species name.
        var guard = Guard((37, "ロコン"));

        Assert.Equal([37], guard.SpeciesNamed("アローラロコン"));
    }

    [Fact]
    public void A_trainer_name_derives_no_species()
    {
        // ボスの指令 = "Boss's Orders" — no species on the TCGdex side, so
        // the guard has nothing to vouch with (the no-guard status, never a
        // silent confirm).
        var guard = Guard((25, "ピカチュウ"), (6, "リザードン"));

        Assert.Empty(guard.SpeciesNamed("ボスの指令"));
    }

    [Fact]
    public void The_same_spelling_from_two_language_rows_derives_once()
    {
        // species_names carries ja and ja-hrkt, usually the same katakana —
        // one spelling, one derivation.
        var guard = Guard((25, "ピカチュウ"), (25, "ピカチュウ"));

        Assert.Equal([25], guard.SpeciesNamed("ピカチュウ"));
    }
}
