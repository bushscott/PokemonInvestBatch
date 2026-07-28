using PokemonInvestBatch.Domain.Parsing;
using PokemonInvestBatch.Domain.Tests.Fixtures;

namespace PokemonInvestBatch.Domain.Tests.Parsing;

public class GradeMonotonicityTests
{
    [Fact]
    public void The_live_charizard_page_satisfies_the_invariant()
    {
        // $370 ≤ $770 ≤ $1,267 ≤ $3,325 ≤ $6,100 ≤ $30,100 on the real page.
        var page = CardDetailParser.Parse(Fixture.Load("charizard-live-a"));

        Assert.Empty(GradeMonotonicity.Violations(page.Chart));
    }

    [Fact]
    public void A_tier_swap_is_reported_as_a_violation()
    {
        // If a remap ever made Grade 9 cost less than Grade 8, every page
        // would still parse cleanly — only this invariant would notice.
        var month = new DateOnly(2026, 7, 1);
        var chart = new Dictionary<PriceTier, IReadOnlyList<PricePoint>>
        {
            [PriceTier.Grade8] = [new PricePoint(month, 500_000)],
            [PriceTier.Grade9] = [new PricePoint(month, 100_000)],
        };

        var violation = Assert.Single(GradeMonotonicity.Violations(chart));
        Assert.Equal(PriceTier.Grade8, violation.Lower);
        Assert.Equal(PriceTier.Grade9, violation.Higher);
    }

    [Fact]
    public void Missing_and_zero_tiers_are_not_violations()
    {
        // Plenty of cards have no graded data; zeros mean "no sales", not
        // "worth nothing".
        var month = new DateOnly(2026, 7, 1);
        var chart = new Dictionary<PriceTier, IReadOnlyList<PricePoint>>
        {
            [PriceTier.Ungraded] = [new PricePoint(month, 50_000)],
            [PriceTier.Grade9] = [new PricePoint(month, 0)],
            [PriceTier.Psa10] = [new PricePoint(month, 200_000)],
        };

        Assert.Empty(GradeMonotonicity.Violations(chart));
    }
}
