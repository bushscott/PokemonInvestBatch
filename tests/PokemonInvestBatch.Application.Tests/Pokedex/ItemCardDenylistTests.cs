using PokemonInvestBatch.Application.Pokedex;

namespace PokemonInvestBatch.Application.Tests.Pokedex;

public class ItemCardDenylistTests
{
    [Theory]
    [InlineData("charizard spirit link #75", true)]
    [InlineData("clefairy doll #70", true)]
    [InlineData("growing grass energy #104", true)]
    [InlineData("dome fossil #155", true)]
    [InlineData("lillie's poke doll #197", true)]
    [InlineData("charizard [1st edition] #4", false)]  // guard rail: species name present, no denylist term
    [InlineData("flareon #13", false)]                  // guard rail: species name present, no denylist term
    public void IsItemCard_MatchesAfterNormalization(string rawTitle, bool expected)
        => Assert.Equal(expected, ItemCardDenylist.IsItemCard(TitleNormalizer.Normalize(rawTitle)));
}
