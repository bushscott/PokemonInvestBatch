using PokemonInvestBatch.Domain.Parsing;

namespace PokemonInvestBatch.Domain.Tests.Parsing;

/// <summary>
/// Every cent stored assumes USD. The server renders USD and converts
/// client-side, so the header dropdown is the page's own statement of what
/// its prices mean — asserted on every parse, spec requirement.
/// </summary>
public class CardDetailParserCurrencyTests
{
    [Fact]
    public void A_page_rendered_in_another_currency_is_rejected_outright()
    {
        var ex = Assert.Throws<SchemaDriftException>(
            () => CardDetailParser.Parse(PageInCurrency("EUR")));

        Assert.Contains("EUR", ex.Message);
    }

    [Fact]
    public void A_page_with_no_currency_selector_is_rejected_as_unprovable()
    {
        // Valid chart data, so the parse genuinely reaches the currency
        // check rather than dying on "not a card page".
        const string html = """<script>VGPC.chart_data = {"used":[[1606806000000,100]]};</script>""";

        var ex = Assert.Throws<SchemaDriftException>(() => CardDetailParser.Parse(html));

        Assert.Contains("currency selector", ex.Message);
    }

    [Fact]
    public void A_usd_page_parses_normally()
    {
        var page = CardDetailParser.Parse(PageInCurrency("USD"));

        Assert.Single(page.Chart);
    }

    private static string PageInCurrency(string currency) => $$"""
        <script>VGPC.chart_data = {"used":[[1606806000000,100]]};</script>
        <ul id="currency_selector"><li><a id="dropdown_selected_currency">{{currency}}</a></li></ul>
        """;
}
