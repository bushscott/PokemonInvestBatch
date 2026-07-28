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
}
