using PokemonInvestBatch.Application.Pokedex;

namespace PokemonInvestBatch.Application.Tests.Pokedex;

public class TitleNormalizerTests
{
    [Theory]
    [InlineData("Charizard [1st Edition] #4", "charizard")]
    [InlineData("Aipom [No Rarity] #67", "aipom")]
    [InlineData("Umbreon VMAX (Alt Art) #215", "umbreon vmax (alt art)")]
    [InlineData("Chien‑Pao #32", "chien-pao")]                    // U+2011 → '-'
    [InlineData("Farfetch’d #27", "farfetch'd")]                  // curly → straight apostrophe
    [InlineData("Flabébé #83", "flabebe")]                        // diacritics folded
    [InlineData("Nidoran♀ #25", "nidoran♀")]                      // gender glyphs PRESERVED
    [InlineData("  Pikachu   &  Zekrom GX  #33", "pikachu & zekrom gx")] // whitespace collapsed
    public void Normalizes(string title, string expected)
        => Assert.Equal(expected, TitleNormalizer.Normalize(title));
}
