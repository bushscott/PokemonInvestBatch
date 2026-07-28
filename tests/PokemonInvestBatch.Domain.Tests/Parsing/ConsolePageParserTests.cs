using PokemonInvestBatch.Domain.Parsing;
using PokemonInvestBatch.Domain.Tests.Fixtures;

namespace PokemonInvestBatch.Domain.Tests.Parsing;

public class ConsolePageParserTests
{
    [Fact]
    public void Parse_reads_150_products_from_a_full_page()
    {
        var page = ConsolePageParser.Parse(Fixture.Load("console-base-set-page1"));

        Assert.Equal(150, page.Products.Count);

        var charizard = Assert.Single(page.Products, p => p.ProductId == 630417);
        Assert.Equal("Charizard #4", charizard.Name);
        Assert.Equal("/game/pokemon-base-set/charizard-4", charizard.Url);
    }

    [Fact]
    public void Parse_exposes_the_next_page_form_verbatim()
    {
        // Pagination is a POST of the site's own hidden form; we re-send its
        // fields exactly rather than guessing query parameters.
        var page = ConsolePageParser.Parse(Fixture.Load("console-base-set-page1"));

        Assert.NotNull(page.NextPageForm);
        var form = page.NextPageForm!;
        Assert.Equal("150", form["cursor"]);
        Assert.True(form.ContainsKey("sort"));
        Assert.True(form.ContainsKey("when"));
        Assert.True(form.ContainsKey("release-date"));
    }

    [Fact]
    public void Parse_yields_450_distinct_products_across_three_cursor_pages()
    {
        var ids = new[] { "console-base-set-page1", "console-base-set-page2", "console-base-set-page3" }
            .SelectMany(f => ConsolePageParser.Parse(Fixture.Load(f)).Products)
            .Select(p => p.ProductId)
            .ToHashSet();

        Assert.Equal(450, ids.Count);
    }

    [Fact]
    public void Parse_returns_null_form_on_the_last_page()
    {
        const string html = """
            <table><tbody>
              <tr id="product-1" data-product="1">
                <td class="title"><a href="/game/x/y-1">Y #1</a></td>
              </tr>
            </tbody></table>
            """;

        var page = ConsolePageParser.Parse(html);

        Assert.Single(page.Products);
        Assert.Null(page.NextPageForm);
    }

    [Fact]
    public void Parse_rejects_product_rows_without_a_title_link()
    {
        const string html = """
            <table><tbody>
              <tr id="product-2" data-product="2"><td class="title">no anchor</td></tr>
            </tbody></table>
            """;

        var ex = Assert.Throws<SchemaDriftException>(() => ConsolePageParser.Parse(html));

        Assert.Contains("product-2", ex.Message);
    }
}
