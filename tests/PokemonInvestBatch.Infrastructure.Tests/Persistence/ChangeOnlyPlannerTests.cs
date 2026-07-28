using PokemonInvestBatch.Domain.Parsing;
using PokemonInvestBatch.Domain.Tests.Fixtures;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Infrastructure.Tests.Persistence;

public class ChangeOnlyPlannerTests
{
    private static readonly DateTimeOffset Observed = new(2026, 7, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void First_visit_appends_every_nonzero_fact_and_no_zeros()
    {
        // Zeros mean "no data yet" — with unknown defaulting to zero, writing
        // them would be the duplicate data the design forbids.
        var page = CardDetailParser.Parse(Fixture.Load("charizard-live-a"));

        var prices = ChangeOnlyPlanner.NewPricePoints(630417, page.Chart, lastKnown: new Dictionary<(PriceTier, DateOnly), int>(), Observed);
        var cells = ChangeOnlyPlanner.NewPopulationCells(630417, page.Population!, lastKnown: new Dictionary<(string, short), int>(), Observed);

        Assert.All(prices, p => Assert.True(p.PriceCents > 0));
        Assert.Contains(prices, p =>
            p.Tier == PriceTier.Ungraded && p.Month == new DateOnly(2020, 12, 1) && p.PriceCents == 22_500);

        // PSA has 10 populated grades, CGC 5 — zeros in CGC's array stay unwritten.
        Assert.Equal(15, cells.Count);
        Assert.Contains(cells, c => c.Grader == "psa" && c.Grade == 10 && c.Population == 486);
        Assert.DoesNotContain(cells, c => c.Population == 0);
    }

    [Fact]
    public void An_identical_revisit_appends_nothing()
    {
        var page = CardDetailParser.Parse(Fixture.Load("charizard-live-a"));
        var firstPrices = ChangeOnlyPlanner.NewPricePoints(630417, page.Chart, new Dictionary<(PriceTier, DateOnly), int>(), Observed);
        var firstCells = ChangeOnlyPlanner.NewPopulationCells(630417, page.Population!, new Dictionary<(string, short), int>(), Observed);

        var knownPrices = firstPrices.ToDictionary(p => (p.Tier, p.Month), p => p.PriceCents);
        var knownCells = firstCells.ToDictionary(c => (c.Grader, c.Grade), c => c.Population);

        var again = CardDetailParser.Parse(Fixture.Load("charizard-live-b"));
        Assert.Empty(ChangeOnlyPlanner.NewPricePoints(630417, again.Chart, knownPrices, Observed.AddDays(1)));
        Assert.Empty(ChangeOnlyPlanner.NewPopulationCells(630417, again.Population!, knownCells, Observed.AddDays(1)));
    }

    [Fact]
    public void A_moved_current_month_appends_exactly_that_row()
    {
        var month = new DateOnly(2026, 7, 1);
        var chart = new Dictionary<PriceTier, IReadOnlyList<PricePoint>>
        {
            [PriceTier.Ungraded] = [new PricePoint(month, 37_037)],
        };
        var lastKnown = new Dictionary<(PriceTier, DateOnly), int>
        {
            [(PriceTier.Ungraded, month)] = 37_000,
        };

        var row = Assert.Single(ChangeOnlyPlanner.NewPricePoints(630417, chart, lastKnown, Observed));
        Assert.Equal(37_037, row.PriceCents);
        Assert.Equal(Observed, row.ObservedAt);
    }

    [Fact]
    public void Population_growth_appends_only_the_changed_cell()
    {
        var population = new PopulationReport
        {
            Psa = [0, 0, 0, 0, 0, 0, 0, 0, 8455, 486],
            Cgc = [0, 0, 0, 0, 0, 0, 0, 0, 0, 4],
        };
        var lastKnown = new Dictionary<(string, short), int>
        {
            [("psa", 9)] = 8455,
            [("psa", 10)] = 480,
            [("cgc", 10)] = 4,
        };

        var cell = Assert.Single(ChangeOnlyPlanner.NewPopulationCells(630417, population, lastKnown, Observed));
        Assert.Equal(("psa", (short)10, 486), (cell.Grader, cell.Grade, cell.Population));
    }
}
