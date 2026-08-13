using PokemonInvestBatch.Application.Enrichment;

namespace PokemonInvestBatch.Application.Tests.Enrichment;

public class SetNameNormalizerTests
{
    [Theory]
    [InlineData("Pokemon Scarlet & Violet", "Scarlet & Violet")]
    [InlineData("Pokemon Go", "Pokémon GO")]
    [InlineData("Pokemon Rumble", "Pokémon Rumble")]
    [InlineData("Pokemon Champion's Path", "Champion's Path")]
    [InlineData("Pokemon Best of Game", "Best of game")]
    [InlineData("Pokemon BREAKthrough", "BREAKthrough")]
    [InlineData("Pokemon Wisdom of Sea & Sky", "Wisdom of Sea and Sky")]
    public void PriceCharting_and_tcgdex_names_of_one_set_normalize_equal(string priceCharting, string tcgdex)
    {
        Assert.Equal(SetNameNormalizer.Normalize(tcgdex), SetNameNormalizer.Normalize(priceCharting));
    }

    [Theory]
    [InlineData("Pokemon Scarlet & Violet 151", "151")]
    [InlineData("Pokemon Team Magma & Team Aqua", "Team Magma vs Team Aqua")]
    [InlineData("Pokemon Expedition", "Expedition Base Set")]
    [InlineData("Pokemon Kalos Starter", "Kalos Starter Set")]
    public void Genuinely_different_names_stay_different(string priceCharting, string tcgdex)
    {
        // These are the alias table's job. Normalization must NOT bridge
        // them — anything that could is a fuzzy matcher, and fuzzy set
        // matching is how Korean 151 silently becomes English 151.
        Assert.NotEqual(SetNameNormalizer.Normalize(tcgdex), SetNameNormalizer.Normalize(priceCharting));
    }

    [Fact]
    public void Only_a_leading_pokemon_word_is_stripped()
    {
        Assert.Equal("2000 WORLD COLLECTION", SetNameNormalizer.Normalize("Pokemon 2000 World Collection"));
        Assert.Equal("DETECTIVE PIKACHU", SetNameNormalizer.Normalize("Detective Pikachu"));
    }
}
