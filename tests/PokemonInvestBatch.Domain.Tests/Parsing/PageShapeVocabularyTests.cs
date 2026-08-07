using PokemonInvestBatch.Domain.Parsing;
using PokemonInvestBatch.Domain.Tests.Fixtures;

namespace PokemonInvestBatch.Domain.Tests.Parsing;

public class PageShapeVocabularyTests
{
    private const string FullCard = """
        {"auction_tiers":["completed-auctions-graded","completed-auctions-used"],
         "chart_data":["graded","manual-only","new","used"],
         "pop_data":["cgc","psa"],
         "vgpc":["category","chart_data","console_uid","pop_data","product"]}
        """;

    [Fact]
    public void A_card_carrying_less_data_brings_no_unfamiliar_names()
    {
        // The false positive this rule exists to kill. An obscure promo with
        // one price tier and no census is a shape never seen before and news
        // to nobody; ten of them buried the alert channel on 2026-08-07.
        var promo = """
            {"auction_tiers":["completed-auctions-used"],
             "chart_data":["used"],
             "pop_data":[],
             "vgpc":["category","chart_data","console_uid","product"]}
            """;

        Assert.Empty(PageShapeVocabulary.NamesAbsentFrom(promo, [FullCard]));
    }

    [Fact]
    public void A_name_seen_in_no_earlier_shape_is_reported()
    {
        var remapped = """
            {"auction_tiers":["completed-auctions-graded","completed-auctions-used"],
             "chart_data":["grade-twenty-three","graded","manual-only","new","used"],
             "pop_data":["cgc","psa"],
             "vgpc":["category","chart_data","console_uid","pop_data","product"]}
            """;

        var unfamiliar = PageShapeVocabulary.NamesAbsentFrom(remapped, [FullCard]);

        Assert.Equal("chart_data:grade-twenty-three", Assert.Single(unfamiliar));
    }

    [Fact]
    public void The_same_name_in_a_different_bucket_is_a_different_name()
    {
        // "psa" as a price tier is not "psa" as a census column. A name that
        // changes bucket is the site rearranging, not a card being quiet.
        var moved = """
            {"auction_tiers":[],"chart_data":["psa"],"pop_data":[],"vgpc":[]}
            """;

        var unfamiliar = PageShapeVocabulary.NamesAbsentFrom(moved, [FullCard]);

        Assert.Equal("chart_data:psa", Assert.Single(unfamiliar));
    }

    [Fact]
    public void Every_known_shape_contributes_to_the_vocabulary()
    {
        // Familiar means seen in ANY earlier shape, not in the newest one:
        // the census columns come from the full card, the tier from the promo.
        var older = """
            {"auction_tiers":["completed-auctions-cib"],"chart_data":[],"pop_data":[],"vgpc":[]}
            """;
        var arrival = """
            {"auction_tiers":["completed-auctions-cib"],
             "chart_data":["used"],
             "pop_data":["psa"],
             "vgpc":["category"]}
            """;

        Assert.Empty(PageShapeVocabulary.NamesAbsentFrom(arrival, [FullCard, older]));
    }

    [Fact]
    public void Of_qualifies_every_name_with_the_bucket_it_came_from()
    {
        var names = PageShapeVocabulary.Of(FullCard);

        Assert.Contains("pop_data:psa", names);
        Assert.Contains("chart_data:graded", names);
        Assert.Contains("auction_tiers:completed-auctions-used", names);
        Assert.DoesNotContain("psa", names);
    }

    [Fact]
    public void A_real_page_is_familiar_to_itself()
    {
        var print = PageFingerprint.OfCardDetailPage(Fixture.Load("charizard-live-a"));

        Assert.Empty(PageShapeVocabulary.NamesAbsentFrom(print.ShapeJson, [print.ShapeJson]));
    }
}
