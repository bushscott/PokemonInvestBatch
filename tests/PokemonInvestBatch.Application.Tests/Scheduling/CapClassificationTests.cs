using PokemonInvestBatch.Application.Scheduling;
using PokemonInvestBatch.Domain.Parsing;

namespace PokemonInvestBatch.Application.Tests.Scheduling;

/// <summary>
/// Every non-synthetic fixture here is a real capped bucket lifted row-for-row
/// from production — the four bulk dumps, the short-page recompositions, and
/// the two organic bursts of August 2026. The organic pages are the load-
/// bearing negatives: Charizard X #223 contains genuine same-day id near-runs
/// (a quarter of its rows), so these tests are what pins the run-gap and the
/// half-page bar to values that real organic traffic cannot cross.
/// </summary>
public class CapClassificationTests
{
    /// <summary>Rows as "sourceId priceCents soldOn" lines — the shape the
    /// production queries print, so a fixture can be pasted from psql.</summary>
    private static List<SaleRecord> Rows(string tier, string lines) =>
        lines.Trim().Split('\n')
            .Select(l => l.Trim().Split(' '))
            .Select(p => new SaleRecord
            {
                Source = "ebay",
                SourceId = p[0],
                PriceCents = int.Parse(p[1]),
                SoldOn = DateOnly.Parse(p[2]),
                GradeTier = tier,
                Title = "x",
            })
            .ToList();

    // Gengar #33, PSA 10, capped 2026-08-15: one seller's graded stack listed
    // in three id neighborhoods, every row sold on one day.
    private const string Gengar = """
        117324238045 12000 2026-08-10
        117324238091 12250 2026-08-10
        117324238117 12050 2026-08-10
        117324238341 12250 2026-08-10
        117324238380 12250 2026-08-10
        117324238398 12750 2026-08-10
        117324238419 12200 2026-08-10
        117324238434 12550 2026-08-10
        117328125814 14750 2026-08-10
        298535700862 12250 2026-08-10
        298535700865 12250 2026-08-10
        298535700886 12100 2026-08-10
        298535700902 12000 2026-08-10
        298535700960 12250 2026-08-10
        298535701034 12100 2026-08-10
        298535701709 12250 2026-08-10
        298535701898 12800 2026-08-10
        298535701968 12250 2026-08-10
        307088691759 13750 2026-08-10
        307088691823 12250 2026-08-10
        307088691864 12250 2026-08-10
        307088691879 11850 2026-08-10
        307088691972 13538 2026-08-10
        307088692107 12150 2026-08-10
        307088692706 12850 2026-08-10
        307088692885 12250 2026-08-10
        307088692978 12150 2026-08-10
        307088692994 13250 2026-08-10
        307088693450 12000 2026-08-10
        307093015365 12250 2026-08-10
        """;

    // Mega Charizard X ex #13, PSA 10, capped 2026-08-14: the id gaps are too
    // wide for the run test — this dump's tell is thirty rows at exactly
    // $40.00, all sold the same day.
    private const string CharizardX13 = """
        278235686564 4000 2026-08-13
        278235689844 4000 2026-08-13
        278235691300 4000 2026-08-13
        278235691649 4000 2026-08-13
        278235695544 4000 2026-08-13
        278235702536 4000 2026-08-13
        278235705177 4000 2026-08-13
        278235705799 4000 2026-08-13
        278235706009 4000 2026-08-13
        278235706248 4000 2026-08-13
        278235706491 4000 2026-08-13
        278235706663 4000 2026-08-13
        278235707013 4000 2026-08-13
        278235747180 4000 2026-08-13
        278235761637 4000 2026-08-13
        278235762280 4000 2026-08-13
        287495785910 4000 2026-08-13
        287495786966 4000 2026-08-13
        287495788798 4000 2026-08-13
        287495789685 4000 2026-08-13
        287495790889 4000 2026-08-13
        287495792152 4000 2026-08-13
        287495795239 4000 2026-08-13
        287495795710 4000 2026-08-13
        287495809965 4000 2026-08-13
        287495813336 4000 2026-08-13
        287495814816 4000 2026-08-13
        287495818651 4000 2026-08-13
        287495821210 4000 2026-08-13
        287495832095 4000 2026-08-13
        """;

    // Jungle Booster Pack, Ungraded, capped 2026-08-21: twenty-six auction
    // lots ("session-lot" ids) across three consecutive sessions, one sale
    // day, prices anything but tight — plus the four-row organic eBay tail.
    private const string BoosterPack = """
        332631-56080 40600 2026-08-20
        332631-56081 38800 2026-08-20
        332631-56082 38000 2026-08-20
        332631-56083 150000 2026-08-20
        332631-56084 93800 2026-08-20
        332632-57072 45000 2026-08-20
        332632-57073 50000 2026-08-20
        332632-57074 42500 2026-08-20
        332632-57075 45000 2026-08-20
        332632-57076 45000 2026-08-20
        332632-57077 47500 2026-08-20
        332632-57078 47500 2026-08-20
        332632-57081 35000 2026-08-20
        332632-57082 30000 2026-08-20
        332633-58065 75000 2026-08-20
        332633-58066 40000 2026-08-20
        332633-58067 45000 2026-08-20
        332633-58068 42500 2026-08-20
        332633-58069 47500 2026-08-20
        332633-58070 68800 2026-08-20
        332633-58071 45000 2026-08-20
        332633-58072 45000 2026-08-20
        332633-58073 45000 2026-08-20
        332633-58074 32500 2026-08-20
        332633-58075 32500 2026-08-20
        332633-58076 68800 2026-08-20
        366603722651 25600 2026-08-21
        366603722833 31100 2026-08-21
        366603723601 25600 2026-08-21
        377415786453 25600 2026-08-21
        """;

    // Mewtwo & Mew GX #SM191, PSA 10, capped 2026-08-16: the first organic
    // burst the ceilings could not schedule around. Scattered ids, no two
    // within a thousand of each other; 90% sold on one day, so the same-day
    // half of the price tell must never fire alone.
    private const string Mewtwo = """
        147493920726 307990 2026-08-15
        147494063992 348999 2026-08-16
        158185225337 339999 2026-08-15
        168612548852 320000 2026-08-15
        188782484198 300000 2026-08-15
        188782936260 340000 2026-08-15
        206482908493 325000 2026-08-15
        206486310254 330000 2026-08-15
        227473264980 350000 2026-08-15
        267756807565 319999 2026-08-15
        267757650266 320000 2026-08-15
        278277145360 329950 2026-08-15
        287515095098 400000 2026-08-15
        287515271165 325000 2026-08-15
        298576137206 309909 2026-08-15
        298578654065 285000 2026-08-16
        318710990620 329999 2026-08-15
        336734322885 319999 2026-08-15
        336743186349 309900 2026-08-15
        358904686387 335000 2026-08-15
        358914013462 320000 2026-08-15
        358923470520 324388 2026-08-15
        366597095729 350000 2026-08-15
        366601064384 335000 2026-08-15
        398271111987 339999 2026-08-15
        398285340944 300000 2026-08-15
        407130608804 330000 2026-08-15
        800482485126 380000 2026-08-15
        800483094934 345000 2026-08-15
        800488501270 325000 2026-08-15
        """;

    // Mega Charizard X ex #223, PSA 10, capped 2026-08-17: an organic burst
    // whose page carries real same-day id near-runs — eight rows sit in
    // clusters under the run gap. This is the fixture that keeps the blocked
    // bar at half the page: loosen either constant and this page turns into a
    // false dump, which would silence a genuine loss.
    private const string CharizardX223 = """
        117336713662 10000 2026-08-17
        117336900913 8700 2026-08-17
        117336902143 9600 2026-08-17
        117336902183 9100 2026-08-17
        117337522610 9000 2026-08-17
        117338436463 10250 2026-08-17
        117343089586 8300 2026-08-17
        117345829189 9010 2026-08-17
        117347086106 8200 2026-08-17
        117347106817 9000 2026-08-17
        117348098444 8400 2026-08-17
        117348098499 9100 2026-08-17
        117348098784 9200 2026-08-17
        178337204794 12344 2026-08-17
        178391622324 10550 2026-08-17
        278269830758 13403 2026-08-17
        287527587823 7000 2026-08-17
        298555929651 9100 2026-08-17
        298566779803 9000 2026-08-17
        298569114954 8900 2026-08-17
        298569115290 8400 2026-08-17
        298569115411 8601 2026-08-17
        298569116388 10250 2026-08-17
        298569116605 8601 2026-08-17
        298571049179 8701 2026-08-17
        298571049198 8400 2026-08-17
        307114114283 8700 2026-08-17
        307114114775 8700 2026-08-17
        307114116618 8601 2026-08-17
        307115272816 9101 2026-08-17
        """;

    // Magneton #301, Ungraded, capped 2026-08-22: a three-row page whose held
    // rows vanished and older ones took their place — the site rewrote it.
    private const string Magneton = """
        147279109965 1039 2026-05-26
        336733421711 195 2026-08-09
        406946048185 301 2026-05-30
        """;

    [Fact]
    public void A_short_page_that_capped_was_recomposed_by_the_site()
    {
        // Zero overlap on a page below the bucket cap cannot be velocity: the
        // site serves every row it has, so nothing scrolled off — our rows
        // were removed at the source. Magneton #301 and the one-row Doublade
        // graded buckets are the production shape.
        Assert.Equal(CapClass.PageRecomposed, CapClassification.Classify(Rows("Ungraded", Magneton)));
        Assert.Equal(CapClass.PageRecomposed, CapClassification.Classify(Rows("Grade 8", "307113829050 2050 2026-08-17")));
    }

    [Fact]
    public void Sequential_id_blocks_on_a_full_page_are_a_bulk_liquidation()
    {
        Assert.Equal(CapClass.BulkLiquidation, CapClassification.Classify(Rows("PSA 10", Gengar)));
    }

    [Fact]
    public void Sequential_auction_lots_are_a_bulk_liquidation()
    {
        Assert.Equal(CapClass.BulkLiquidation, CapClassification.Classify(Rows("Ungraded", BoosterPack)));
    }

    [Fact]
    public void One_price_sold_in_one_day_across_a_full_page_is_a_bulk_liquidation()
    {
        Assert.Equal(CapClass.BulkLiquidation, CapClassification.Classify(Rows("PSA 10", CharizardX13)));
    }

    [Fact]
    public void An_organic_burst_with_scattered_ids_stays_organic()
    {
        Assert.Equal(CapClass.OrganicBurst, CapClassification.Classify(Rows("PSA 10", Mewtwo)));
    }

    [Fact]
    public void Real_id_near_runs_inside_an_organic_burst_do_not_make_it_a_dump()
    {
        Assert.Equal(CapClass.OrganicBurst, CapClassification.Classify(Rows("PSA 10", CharizardX223)));
    }

    [Fact]
    public void Non_numeric_ids_never_join_a_run()
    {
        // PriceCharting's synthetic ids for non-eBay sources are base64-ish
        // strings; adjacent-looking strings prove nothing about the seller.
        // A full page of them with scattered prices and days stays organic.
        var rows = Enumerable.Range(0, 30)
            .Select(i => new SaleRecord
            {
                Source = "pricecharting",
                SourceId = $"aOepPqpZqR{(char)('A' + i)}",
                PriceCents = 700 + i * 13,
                SoldOn = new DateOnly(2026, 8, 1).AddDays(i % 15),
                GradeTier = "Ungraded",
                Title = "x",
            })
            .ToList();

        Assert.Equal(CapClass.OrganicBurst, CapClassification.Classify(rows));
    }
}
