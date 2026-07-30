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

    // Whatever lands in ProductListing.Url is what the detail lane will
    // blindly fetch — hostile hrefs must die here, loudly, as drift.

    [Theory]
    [InlineData("https://evil.example/game/x")]
    [InlineData("//evil.example/game/x")]
    [InlineData("/stripe-connect/x")]
    [InlineData("/game/../stripe-connect")]
    public void Parse_rejects_hrefs_that_could_aim_the_crawler_elsewhere(string href)
    {
        var ex = Assert.Throws<SchemaDriftException>(
            () => ConsolePageParser.Parse(SingleProductPage(href)));

        Assert.Contains("refusing to store", ex.Message);
    }

    [Fact]
    public void Parse_rejects_an_href_longer_than_the_url_column()
    {
        var href = "/game/pokemon-base-set/" + new string('x', 600);

        var ex = Assert.Throws<SchemaDriftException>(
            () => ConsolePageParser.Parse(SingleProductPage(href)));

        Assert.Contains("623", ex.Message);
    }

    [Fact]
    public void Parse_accepts_an_ordinary_game_href()
    {
        var page = ConsolePageParser.Parse(SingleProductPage("/game/pokemon-base-set/charizard-4"));

        Assert.Equal("/game/pokemon-base-set/charizard-4", Assert.Single(page.Products).Url);
    }

    private static string SingleProductPage(string href) => $"""
        <table><tbody>
          <tr id="product-630417"><td class="title"><a href="{href}">Charizard #4</a></td></tr>
        </tbody></table>
        """;

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
