namespace PokemonInvestBatch.Domain.Parsing;

/// <summary>A pair of tiers whose latest prices are out of order.</summary>
public readonly record struct MonotonicityViolation(PriceTier Lower, PriceTier Higher, int LowerCents, int HigherCents);

/// <summary>
/// Value invariant (integrity layer 4): a higher grade selling cheaper than
/// a lower grade (dashboard wording). A single violation is thin-market
/// noise — a stale high sale on one tier vs a fresh low sale on another —
/// and is expected; only a step change in the rate across all cards matters,
/// because that means the site silently remapped which chart series is
/// which grade while every page still parses cleanly.
/// </summary>
public static class GradeMonotonicity
{
    // Ascending grade order; PriceTier's declaration order is that order.
    private static readonly PriceTier[] Ascending =
        [.. Enum.GetValues<PriceTier>().OrderBy(t => (int)t)];

    public static IReadOnlyList<MonotonicityViolation> Violations(
        IReadOnlyDictionary<PriceTier, IReadOnlyList<PricePoint>> chart)
    {
        // Latest non-zero price per tier; zero means "no sales", not "worthless".
        var latest = Ascending
            .Where(tier => chart.TryGetValue(tier, out var points)
                && points.Count > 0
                && points[^1].PriceCents > 0)
            .Select(tier => (Tier: tier, Cents: chart[tier][^1].PriceCents))
            .ToList();

        var violations = new List<MonotonicityViolation>();
        for (var i = 1; i < latest.Count; i++)
        {
            var (lower, lowerCents) = latest[i - 1];
            var (higher, higherCents) = latest[i];
            if (higherCents < lowerCents)
            {
                violations.Add(new MonotonicityViolation(lower, higher, lowerCents, higherCents));
            }
        }

        return violations;
    }
}
