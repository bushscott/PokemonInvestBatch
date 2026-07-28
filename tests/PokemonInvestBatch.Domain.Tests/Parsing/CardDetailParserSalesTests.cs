using PokemonInvestBatch.Domain.Parsing;
using PokemonInvestBatch.Domain.Tests.Fixtures;

namespace PokemonInvestBatch.Domain.Tests.Parsing;

public class CardDetailParserSalesTests
{
    // Counts verified independently against the live capture:
    // 368 ebay + 20 tcgplayer + 19 goldin + 2 heritage + 1 pwcc = 410.
    [Fact]
    public void Parse_reads_all_sales_across_all_marketplaces()
    {
        var page = CardDetailParser.Parse(Fixture.Load("charizard-live-a"));

        Assert.Equal(410, page.Sales.Count);
        Assert.Equal(368, page.Sales.Count(s => s.Source == "ebay"));
        Assert.Equal(20, page.Sales.Count(s => s.Source == "tcgplayer"));
        Assert.Equal(19, page.Sales.Count(s => s.Source == "goldin"));
        Assert.Equal(2, page.Sales.Count(s => s.Source == "heritage"));
        Assert.Equal(1, page.Sales.Count(s => s.Source == "pwcc"));
    }

    [Fact]
    public void Parse_reads_a_known_ebay_sale_with_its_tier_label()
    {
        var page = CardDetailParser.Parse(Fixture.Load("charizard-live-a"));

        var sale = Assert.Single(page.Sales, s => s.SourceId == "236952130094");
        Assert.Equal("ebay", sale.Source);
        Assert.Equal(new DateOnly(2026, 7, 28), sale.SoldOn);
        Assert.Equal(29_300, sale.PriceCents);
        Assert.Null(sale.ListedPriceCents);
        Assert.Equal("Ungraded", sale.GradeTier);
        Assert.Contains("Charizard 4/102", sale.Title);
    }

    [Fact]
    public void Parse_reads_a_known_tcgplayer_sale()
    {
        var page = CardDetailParser.Parse(Fixture.Load("charizard-live-a"));

        var sale = Assert.Single(page.Sales, s => s.SourceId == "SXICICw4sjns");
        Assert.Equal("tcgplayer", sale.Source);
        Assert.Equal(new DateOnly(2026, 7, 24), sale.SoldOn);
        Assert.Equal(55_000, sale.PriceCents);
    }

    [Fact]
    public void Parse_decodes_html_entities_in_source_ids()
    {
        // Some tcgplayer ids carry entities like &#39; — the dedup key must be
        // the decoded value or re-scrapes would mint phantom duplicates.
        var page = CardDetailParser.Parse(Fixture.Load("charizard-live-a"));

        Assert.DoesNotContain(page.Sales, s => s.SourceId.Contains("&#"));
    }

    [Fact]
    public void Parse_assigns_every_sale_a_tier_label_from_the_page_itself()
    {
        var page = CardDetailParser.Parse(Fixture.Load("charizard-live-a"));

        Assert.All(page.Sales, s => Assert.False(string.IsNullOrWhiteSpace(s.GradeTier)));
        // Charizard's PSA 10 bucket holds 30 completed sales.
        Assert.Equal(30, page.Sales.Count(s => s.GradeTier == "PSA 10"));
    }

    [Fact]
    public void Parse_rejects_sales_from_unknown_marketplaces()
    {
        // A sixth marketplace appearing must alert, never be silently dropped.
        const string html = """
            <script>VGPC.chart_data = {"used":[[1606806000000,100]]};</script>
            <select id="completed-auctions-condition">
              <option value="completed-auctions-used">Ungraded (1)</option>
            </select>
            <table class="hoverable-rows sortable"><tbody>
              <tr id="mercari-999"><td class="date">2026-07-01</td>
                <td class="title"><a>Something</a></td>
                <td class="numeric"><span class="js-price">$10.00</span></td>
              </tr>
            </tbody></table>
            """;

        var ex = Assert.Throws<SchemaDriftException>(() => CardDetailParser.Parse(html));

        Assert.Contains("mercari", ex.Message);
    }
}
