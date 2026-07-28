using PokemonInvestBatch.Domain.Parsing;
using PokemonInvestBatch.Domain.Tests.Fixtures;

namespace PokemonInvestBatch.Domain.Tests.Parsing;

public class CardDetailParserImageTests
{
    [Fact]
    public void Parse_extracts_the_product_image_hash()
    {
        // One image per card; the CDN hash segment is its content address
        // and the fetch-once key.
        var page = CardDetailParser.Parse(Fixture.Load("charizard-live-a"));

        Assert.Equal("hpgpcpsd42huitud", page.ImageHash);
    }

    [Fact]
    public void Pages_without_a_product_image_yield_null()
    {
        const string html = """<script>VGPC.chart_data = {"used":[[1606806000000,100]]};</script>""";

        Assert.Null(CardDetailParser.Parse(html).ImageHash);
    }
}
