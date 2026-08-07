using PokemonInvestBatch.Domain.Parsing;
using PokemonInvestBatch.Domain.Tests.Fixtures;

namespace PokemonInvestBatch.Domain.Tests.Parsing;

public class CardDetailParserChartTests
{
    // Expected values read directly off the live capture's embedded JSON,
    // cross-checked against the rendered page: Ungraded $370.37, PSA 10 $30,100.
    [Fact]
    public void Parse_reads_all_six_monthly_series_in_cents()
    {
        var page = CardDetailParser.Parse(Fixture.Load("charizard-live-a"));

        var chart = page.Chart;
        Assert.Equal(6, chart.Count);

        var ungraded = chart[PriceTier.Ungraded];
        Assert.Equal(68, ungraded.Count);
        Assert.Equal(new PricePoint(new DateOnly(2020, 12, 1), 22_500), ungraded[0]);
        Assert.Equal(new PricePoint(new DateOnly(2026, 7, 1), 37_037), ungraded[^1]);

        Assert.Equal(3_010_000, chart[PriceTier.Psa10][^1].PriceCents);
        Assert.Equal(332_500, chart[PriceTier.Grade9][^1].PriceCents);
        Assert.Equal(77_000, chart[PriceTier.Grade7][^1].PriceCents);
        Assert.Equal(126_750, chart[PriceTier.Grade8][^1].PriceCents);
        Assert.Equal(610_000, chart[PriceTier.Grade9Half][^1].PriceCents);
    }

    [Fact]
    public void A_famous_card_carries_every_tier_the_canary_demands()
    {
        // The canary names the tiers it expects rather than counting them, so
        // a single tier can no longer go missing in silence. That assertion is
        // only safe while a real, liquid card genuinely carries all six —
        // this is that premise, pinned to a live capture.
        var page = CardDetailParser.Parse(Fixture.Load("charizard-live-a"));

        Assert.DoesNotContain(Enum.GetValues<PriceTier>(), t => !page.Chart.ContainsKey(t));
    }

    [Fact]
    public void Parse_rejects_unknown_chart_series_keys()
    {
        // Synthetic drift: a series key we have no tier mapping for must fail
        // loudly, never be skipped — same rule as the pop_data schema change.
        const string html = """<script>VGPC.chart_data = {"used":[[1606806000000,100]],"mystery":[[1606806000000,200]]};</script>""";

        var ex = Assert.Throws<SchemaDriftException>(() => CardDetailParser.Parse(html));

        Assert.Contains("mystery", ex.Message);
    }

    [Fact]
    public void Parse_rejects_pages_with_no_chart_data()
    {
        // A /game/ page without VGPC.chart_data is not a card page we
        // understand — refusing beats writing a card with no history.
        var ex = Assert.Throws<SchemaDriftException>(() => CardDetailParser.Parse("<html><body>hi</body></html>"));

        Assert.Contains("chart_data", ex.Message);
    }
}
