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
            <a id="dropdown_selected_currency">USD</a>
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

    // Every failure below must surface as SchemaDriftException — any other
    // type escapes the crawl lane's quarantine handling and the card is
    // retried forever (the poison-card livelock).

    [Fact]
    public void Parse_flags_a_malformed_sale_date_as_drift()
    {
        var ex = Assert.Throws<SchemaDriftException>(
            () => CardDetailParser.Parse(SingleSalePage(date: "07/01/2026")));

        Assert.Contains("07/01/2026", ex.Message);
    }

    [Fact]
    public void Parse_flags_garbage_price_text_as_drift()
    {
        var ex = Assert.Throws<SchemaDriftException>(
            () => CardDetailParser.Parse(SingleSalePage(price: "$1.2.3")));

        Assert.Contains("$1.2.3", ex.Message);
    }

    [Fact]
    public void Parse_flags_a_price_too_large_for_a_cents_column_as_drift()
    {
        // $99,999,999,999 is ~4,600x the record card sale; a real listing
        // will never hit this, so it can only be page breakage.
        var ex = Assert.Throws<SchemaDriftException>(
            () => CardDetailParser.Parse(SingleSalePage(price: "$99,999,999,999.00")));

        Assert.Contains("exceeds", ex.Message);
    }

    [Fact]
    public void Parse_flags_an_oversized_marketplace_id_as_drift()
    {
        var ex = Assert.Throws<SchemaDriftException>(
            () => CardDetailParser.Parse(SingleSalePage(rowId: $"ebay-{new string('x', 250)}")));

        Assert.Contains("250", ex.Message);
    }

    [Fact]
    public void Parse_clips_an_absurd_title_instead_of_rejecting_the_page()
    {
        // Titles are display text, not identity — a hostile 600-char title
        // must not bench an otherwise-good card.
        var page = CardDetailParser.Parse(SingleSalePage(title: new string('x', 600)));

        var sale = Assert.Single(page.Sales);
        Assert.Equal(SaleRecord.MaxTitleLength, sale.Title.Length);
    }

    [Fact]
    public void Parse_wraps_any_unexpected_exception_as_drift()
    {
        // A chart value beyond int.MaxValue makes GetInt32 throw
        // FormatException deep inside the parser; the Parse-level contract
        // must convert it (and anything like it) into drift.
        const string html = """<script>VGPC.chart_data = {"used":[[1606806000000,99999999999]]};</script>""";

        var ex = Assert.Throws<SchemaDriftException>(() => CardDetailParser.Parse(html));

        Assert.Contains("Unexpected", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    private static string SingleSalePage(
        string rowId = "ebay-123",
        string date = "2026-07-01",
        string price = "$10.00",
        string title = "Something") => $$"""
        <script>VGPC.chart_data = {"used":[[1606806000000,100]]};</script>
        <a id="dropdown_selected_currency">USD</a>
        <select id="completed-auctions-condition">
          <option value="completed-auctions-used">Ungraded (1)</option>
        </select>
        <table class="hoverable-rows sortable"><tbody>
          <tr id="{{rowId}}"><td class="date">{{date}}</td>
            <td class="title"><a>{{title}}</a></td>
            <td class="numeric"><span class="js-price">{{price}}</span></td>
          </tr>
        </tbody></table>
        """;
}
