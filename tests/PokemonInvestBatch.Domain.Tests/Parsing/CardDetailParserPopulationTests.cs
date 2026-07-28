using PokemonInvestBatch.Domain.Parsing;
using PokemonInvestBatch.Domain.Tests.Fixtures;

namespace PokemonInvestBatch.Domain.Tests.Parsing;

public class CardDetailParserPopulationTests
{
    [Fact]
    public void Parse_rejects_unknown_population_keys_from_pre_2026_schema()
    {
        // Wayback capture of 2024-06-01: pop_data was {"pop":[...]} before
        // PriceCharting split it into {"psa":[...],"cgc":[...]}. An unknown
        // key must fail loudly (integrity layer 2), never be skipped.
        var html = Fixture.Load("charizard-2024-06-pop-schema");

        var ex = Assert.Throws<SchemaDriftException>(() => CardDetailParser.Parse(html));

        Assert.Contains("pop", ex.Message);
    }

    [Fact]
    public void Parse_reads_psa_and_cgc_population_by_grade()
    {
        // Live capture 2026-07-27. Index i = grade i+1 (verified against the
        // site's own chart JS: xAxis categories are grades '1'..'10').
        var html = Fixture.Load("charizard-live-a");

        var page = CardDetailParser.Parse(html);

        Assert.NotNull(page.Population);
        var pop = page.Population!;
        Assert.Equal(486, pop.Psa[9]);    // PSA 10
        Assert.Equal(8455, pop.Psa[8]);   // PSA 9
        Assert.Equal(4096, pop.Psa[0]);   // PSA 1
        Assert.Equal(4, pop.Cgc[9]);      // CGC 10
        Assert.Equal(10, pop.Psa.Count);
        Assert.Equal(10, pop.Cgc.Count);
    }

    [Fact]
    public void Parse_is_deterministic_across_identical_fetches()
    {
        var a = CardDetailParser.Parse(Fixture.Load("charizard-live-a"));
        var b = CardDetailParser.Parse(Fixture.Load("charizard-live-b"));

        Assert.Equal(a.Population, b.Population);
    }
}
