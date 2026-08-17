using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Application.Scheduling;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Integration.Tests;

/// <summary>
/// The burn-window rule is written twice — once as C# in VisitPriority.Score,
/// once as an EF expression in VisitCandidatePool.DueByBurnWindow that Postgres
/// has to execute. They cannot share code: the safety fraction now depends on a
/// column, and EF cannot translate a method call over entity data, so the
/// ternary is spelled out in both places.
///
/// That duplication is load-bearing. If the pool's inequality drifts looser than
/// the scorer's, hot cards stop being loaded into the candidate pool at all and
/// the scorer never gets the chance to rank them — the zero-missed-sales
/// guarantee fails silently, with every test that only exercises Score still
/// green. These tests pin the two together across the boundary cases.
/// </summary>
public class BurnWindowQueryAgreementTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly VisitPriorityOptions Options = new();

    private const double BurnDueTier = 3_000_000;

    private static Card Card(long id, double salesPerDay, double daysSinceVisit, bool nearMiss = false) => new()
    {
        Id = id,
        Url = $"/game/set/card-{id}",
        Name = $"card {id}",
        ObservedSalesPerDay = salesPerDay,
        LastVisitedAt = Now.AddDays(-daysSinceVisit),
        NearMissAt = nearMiss ? Now.AddDays(-daysSinceVisit) : null,
    };

    /// <summary>Rates and stalenesses that straddle every edge that matters:
    /// either side of the hot-rate threshold, of the two ceiling bands, and of
    /// both fractions. The 7.33/3.9 pair is Kecleon #88's 2026-08-11 loss and
    /// the 1.57/12.2 pair is the cohort that outranked it; 1.99/2.01 straddle
    /// FastCeilingRate and the 0.75/1.5/3.0 stalenesses sit on the ceiling and
    /// half-ceiling lines the near-miss leash creates (2.0 stays as the
    /// retired fast-ceiling line the 2026-08-17 tightening moved off of).</summary>
    private static readonly double[] Rates =
        [0.05, 0.5, 0.99, 1.0, 1.01, 1.57, 1.99, 2.0, 2.01, 3.0, 4.5, 7.33, 15.0, 30.0];

    private static readonly double[] Stalenesses =
        [0.1, 0.5, 0.75, 1.0, 1.5, 1.637, 2.0, 2.046, 3.0, 3.9, 4.0, 12.0, 12.2, 15.0, 29.0, 31.0];

    public static TheoryData<double, double, bool> Grid()
    {
        var data = new TheoryData<double, double, bool>();
        foreach (var rate in Rates)
        {
            foreach (var days in Stalenesses)
            {
                data.Add(rate, days, false);
                data.Add(rate, days, true);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void The_pool_query_selects_exactly_the_cards_the_scorer_calls_due(
        double rate, double days, bool nearMiss)
    {
        var card = Card(1, rate, days, nearMiss);

        var pool = VisitCandidatePool
            .DueByBurnWindow(new[] { card }.AsQueryable(), Now, Options)
            .Any();

        var scored = VisitPriority.Score(
            new CardVisitState
            {
                LastVisitedAt = card.LastVisitedAt,
                ObservedSalesPerDay = card.ObservedSalesPerDay,
                NearMiss = nearMiss,
            },
            Now,
            Options) >= BurnDueTier;

        Assert.True(
            pool == scored,
            $"rate {rate}/day at {days}d (near miss: {nearMiss}): pool says {pool}, scorer says {scored}");
    }

    /// <summary>
    /// Membership agreement is not enough. The pool's window is bounded at
    /// TierTake, so under a backlog the tier is served in the order the query
    /// returns — and re-ranked by the scorer once it arrives. If the two orders
    /// disagree, the scorer keeps picking whichever end it prefers while the
    /// query keeps handing back the same unserved cards at the other end, and
    /// they burn. That is exactly how Kecleon #88 lost rows on 2026-08-11: the
    /// query ranked it 8th by rows burned, the scorer ranked it below 172 cards
    /// that had waited longer but burned less, and it sat for seventeen hours.
    /// </summary>
    [Fact]
    public void The_pool_query_hands_back_burn_due_cards_in_the_order_the_scorer_picks_them()
    {
        var cards = Rates
            .SelectMany(_ => Stalenesses, (rate, days) => (rate, days))
            .Select((pair, i) => Card(i + 1, pair.rate, pair.days))
            .ToList();

        var poolOrder = VisitCandidatePool
            .DueByBurnWindow(cards.AsQueryable(), Now, Options)
            .Select(c => c.Id)
            .ToList();

        var scorerOrder = poolOrder
            .Select(id => cards.Single(c => c.Id == id))
            .OrderByDescending(c => VisitPriority.Score(
                new CardVisitState
                {
                    LastVisitedAt = c.LastVisitedAt,
                    ObservedSalesPerDay = c.ObservedSalesPerDay,
                },
                Now,
                Options))
            .Select(c => c.Id)
            .ToList();

        Assert.NotEmpty(poolOrder);
        Assert.Equal(poolOrder, scorerOrder);
    }

    [Fact]
    public void The_pool_query_still_translates_to_sql()
    {
        // The agreement test above runs the expression as LINQ-to-Objects, which
        // proves the logic matches but not that Postgres can run it — a CASE over
        // a nullable double is exactly the kind of thing that translates fine in
        // memory and throws on the Pi. ToQueryString needs no connection.
        var options = new DbContextOptionsBuilder<PokemonDbContext>()
            .UseNpgsql("Host=localhost;Database=translation-check-only")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new PokemonDbContext(options);

        var sql = VisitCandidatePool.DueByBurnWindow(db.Cards, Now, Options).ToQueryString();

        Assert.Contains("CASE", sql);
        Assert.Contains("observed_sales_per_day", sql);
        // The ceiling (Math.Min → LEAST) and the near-miss leash must survive
        // translation too — losing either would loosen admission silently.
        Assert.Contains("LEAST", sql);
        Assert.Contains("near_miss_at", sql);
    }
}
