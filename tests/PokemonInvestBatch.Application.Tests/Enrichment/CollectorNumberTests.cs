using PokemonInvestBatch.Application.Enrichment;

namespace PokemonInvestBatch.Application.Tests.Enrichment;

public class CollectorNumberTests
{
    [Theory]
    [InlineData("215", "215")]
    [InlineData("053", "53")]
    [InlineData("001", "1")]
    [InlineData("0", "0")]
    [InlineData("000", "0")]
    [InlineData("TG04", "TG4")]
    [InlineData("tg23", "TG23")]
    [InlineData("SWSH062", "SWSH62")]
    [InlineData("H14", "H14")]
    [InlineData("CC002", "CC2")]
    public void Canonical_uppercases_and_drops_leading_zeros(string raw, string canonical)
    {
        Assert.Equal(canonical, CollectorNumber.Canonical(raw));
    }

    [Fact]
    public void PriceCharting_and_tcgdex_spellings_of_one_number_meet()
    {
        // PC lists "Mew ex #53" where svp's localId is "053".
        Assert.Equal(CollectorNumber.Canonical("53"), CollectorNumber.Canonical("053"));
        Assert.Equal(CollectorNumber.Canonical("TG4"), CollectorNumber.Canonical("TG04"));
    }

    [Theory]
    [InlineData("TG23", "TG")]
    [InlineData("swsh262", "SWSH")]
    [InlineData("215", "")]
    [InlineData("H14", "H")]
    [InlineData("GG70", "GG")]
    public void The_alpha_prefix_is_the_routing_key(string number, string prefix)
    {
        Assert.Equal(prefix, CollectorNumber.AlphaPrefix(number));
    }
}
