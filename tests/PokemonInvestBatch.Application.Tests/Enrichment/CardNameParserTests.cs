using PokemonInvestBatch.Application.Enrichment;

namespace PokemonInvestBatch.Application.Tests.Enrichment;

public class CardNameParserTests
{
    [Theory]
    [InlineData("Umbreon VMAX #215", "Umbreon VMAX", "215")]
    [InlineData("Umbreon VMAX #TG23", "Umbreon VMAX", "TG23")]
    [InlineData("Lance's Charizard V #SWSH133", "Lance's Charizard V", "SWSH133")]
    [InlineData("Mew ex #53", "Mew ex", "53")]
    [InlineData("Aquapolis Steelix #H24", "Aquapolis Steelix", "H24")]
    public void Reads_the_number_after_the_final_hash(string name, string baseName, string number)
    {
        var parts = CardNameParser.Parse(name);

        Assert.Equal(baseName, parts.BaseName);
        Assert.Equal(number, parts.Number);
        Assert.Empty(parts.VariantTags);
    }

    [Theory]
    [InlineData("Charizard [Shadowless] #4", "Charizard", "Shadowless", "4")]
    [InlineData("Charizard [1st Edition] #4", "Charizard", "1st Edition", "4")]
    [InlineData("Nidoran [1st Edition] #55", "Nidoran", "1st Edition", "55")]
    public void Variant_tags_come_out_of_the_base_name(string name, string baseName, string tag, string number)
    {
        var parts = CardNameParser.Parse(name);

        Assert.Equal(baseName, parts.BaseName);
        Assert.Equal([tag], parts.VariantTags);
        Assert.Equal(number, parts.Number);
    }

    [Theory]
    [InlineData("Booster Box [1st Edition]", "Booster Box")]
    [InlineData("Ancient Mew", "Ancient Mew")]
    [InlineData("Unown [A]", "Unown")]
    [InlineData("Pocket Pikachu Console", "Pocket Pikachu Console")]
    public void No_hash_means_no_number_not_a_guess(string name, string baseName)
    {
        var parts = CardNameParser.Parse(name);

        Assert.Equal(baseName, parts.BaseName);
        Assert.Null(parts.Number);
    }

    [Fact]
    public void A_hash_followed_by_prose_is_not_a_number()
    {
        var parts = CardNameParser.Parse("Weird Product # not a number");

        Assert.Null(parts.Number);
        Assert.Equal("Weird Product # not a number", parts.BaseName);
    }

    [Fact]
    public void Whitespace_collapses_in_the_base_name()
    {
        var parts = CardNameParser.Parse("  Umbreon   VMAX  #215 ");

        Assert.Equal("Umbreon VMAX", parts.BaseName);
        Assert.Equal("215", parts.Number);
    }

    [Fact]
    public void An_unclosed_bracket_stays_literal()
    {
        var parts = CardNameParser.Parse("Charizard [Shadowless #4");

        Assert.Equal("Charizard [Shadowless", parts.BaseName);
        Assert.Equal("4", parts.Number);
        Assert.Empty(parts.VariantTags);
    }

    [Fact]
    public void A_tag_after_the_number_still_parses()
    {
        var parts = CardNameParser.Parse("Charizard #4 [Error]");

        Assert.Equal("Charizard", parts.BaseName);
        Assert.Equal("4", parts.Number);
        Assert.Equal(["Error"], parts.VariantTags);
    }
}
