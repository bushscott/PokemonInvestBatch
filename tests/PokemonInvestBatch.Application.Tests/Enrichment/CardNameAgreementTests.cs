using PokemonInvestBatch.Application.Enrichment;

namespace PokemonInvestBatch.Application.Tests.Enrichment;

/// <summary>
/// Every positive case here is a measured disagreement class from the
/// executed 283-card research join (2026-08-12): real pairs where the two
/// catalogs name the same physical card differently.
/// </summary>
public class CardNameAgreementTests
{
    [Theory]
    [InlineData("Electric Energy", "Lightning Energy")]
    [InlineData("Dark Energy", "Darkness Energy")]
    [InlineData("Steel Energy", "Metal Energy")]
    [InlineData("Nidoran", "Nidoran♂")]
    [InlineData("Nidoran", "Nidoran♀")]
    [InlineData("Pokemon Breeder", "Pokémon Breeder")]
    [InlineData("Charizard VStar", "Charizard VSTAR")]
    [InlineData("Mewtwo & Mew GX", "Mewtwo & Mew-GX")]
    [InlineData("Flabebe", "Flabébé")]
    [InlineData("Mr Mime", "Mr. Mime")]
    [InlineData("Farfetch'd", "Farfetch'd")]
    [InlineData("Umbreon VMAX", "Umbreon VMAX")]
    public void Known_synonym_classes_agree(string priceCharting, string tcgdex)
    {
        Assert.True(CardNameAgreement.Agree(priceCharting, tcgdex));
    }

    [Fact]
    public void Symmetric_substitution_cannot_break_a_real_equality()
    {
        // "Dark Charizard" is a Team Rocket card name, not an energy synonym;
        // both sides substitute identically so they still meet.
        Assert.True(CardNameAgreement.Agree("Dark Charizard", "Dark Charizard"));
    }

    [Theory]
    [InlineData("Charizard", "Palkia")]
    [InlineData("Umbreon VMAX", "Umbreon V")]
    [InlineData("Pikachu", "Raichu")]
    public void Different_cards_never_agree(string priceCharting, string tcgdex)
    {
        // The gate's whole job: a number hit with a disagreeing name must
        // refuse — this is what catches Celebrations Classic Collection.
        Assert.False(CardNameAgreement.Agree(priceCharting, tcgdex));
    }
}
