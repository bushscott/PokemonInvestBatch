using PokemonInvestBatch.Application.Scheduling;

namespace PokemonInvestBatch.Application.Tests.Scheduling;

/// <summary>
/// Simulation stress tests: the real scoring function driven as a greedy
/// scheduler over a mixed population for simulated months, asserting the
/// zero-missed-sales guarantee holds — including under budget scarcity,
/// where the system must triage correctly (cold cards wait; selling cards
/// never lose rows).
/// </summary>
public class VisitSchedulingStressTests
{
    private sealed class SimCard
    {
        public double SalesPerDay { get; init; }

        public DateTimeOffset LastVisited { get; set; }
    }

    private static readonly DateTimeOffset T0 = new(2026, 7, 29, 0, 0, 0, TimeSpan.Zero);

    private static readonly VisitPriorityOptions Options = new();

    /// <summary>Five hot cards (3 sales/day → 10-day burn windows) among
    /// forty-five quiet ones, all recently visited at staggered moments.</summary>
    private static List<SimCard> Population() =>
        [.. Enumerable.Range(0, 50).Select(i => new SimCard
        {
            SalesPerDay = i < 5 ? 3.0 : 0,
            LastVisited = T0.AddDays(-0.1 * i),
        })];

    /// <summary>One visit per step, steps sized to the daily budget; every
    /// card's gap is checked against the invariant before each pick.</summary>
    private static void RunSimulation(
        List<SimCard> cards, double visitsPerDay, int days, Action<SimCard, double> assertInvariant)
    {
        var stepHours = 24.0 / visitsPerDay;
        var now = T0;
        for (var step = 0; step < days * visitsPerDay; step++)
        {
            now = now.AddHours(stepHours);
            foreach (var card in cards)
            {
                assertInvariant(card, (now - card.LastVisited).TotalDays);
            }

            var pick = cards.MaxBy(c => VisitPriority.Score(
                new CardVisitState
                {
                    LastVisitedAt = c.LastVisited,
                    ObservedSalesPerDay = c.SalesPerDay,
                    AnyBucketAtCap = false,
                },
                now,
                Options))!;
            pick.LastVisited = now;
        }
    }

    [Fact]
    public void With_a_healthy_budget_no_card_outruns_its_window_or_the_floor()
    {
        RunSimulation(Population(), visitsPerDay: 8, days: 90, (card, gapDays) =>
        {
            if (card.SalesPerDay > 0)
            {
                var burnWindow = SalesObservation.BucketCap / card.SalesPerDay;
                Assert.True(
                    gapDays < burnWindow,
                    $"selling card waited {gapDays:F1}d — past its {burnWindow:F1}d burn window");
            }
            else
            {
                Assert.True(
                    gapDays < Options.MaxDaysBetweenVisits + 3,
                    $"quiet card waited {gapDays:F1}d — far past the starvation floor");
            }
        });
    }

    [Fact]
    public void Under_a_starved_budget_selling_cards_are_protected_first()
    {
        // Two visits/day cannot serve everyone: the cold floor alone wants
        // 1.5/day and the hot cards ~1/day. Correct triage sacrifices the
        // floor — quiet cards wait, losing nothing — while no selling card
        // ever exceeds its burn window.
        RunSimulation(Population(), visitsPerDay: 2, days: 90, (card, gapDays) =>
        {
            if (card.SalesPerDay > 0)
            {
                var burnWindow = SalesObservation.BucketCap / card.SalesPerDay;
                Assert.True(
                    gapDays < burnWindow,
                    $"selling card waited {gapDays:F1}d under scarcity — past its {burnWindow:F1}d burn window");
            }
        });
    }
}
