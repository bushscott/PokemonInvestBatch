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

    private static Card Card(long id, double salesPerDay, double daysSinceVisit) => new()
    {
        Id = id,
        Url = $"/game/set/card-{id}",
        Name = $"card {id}",
        ObservedSalesPerDay = salesPerDay,
        LastVisitedAt = Now.AddDays(-daysSinceVisit),
    };

    /// <summary>Rates and stalenesses that straddle every edge that matters:
    /// either side of the hot-rate threshold, and either side of both the
    /// tightened and the original fraction.</summary>
    public static TheoryData<double, double> Grid()
    {
        var data = new TheoryData<double, double>();
        foreach (var rate in new[] { 0.05, 0.5, 0.99, 1.0, 1.01, 3.0, 7.33, 15.0, 30.0 })
        {
            foreach (var days in new[] { 0.1, 0.5, 1.0, 1.637, 2.0, 2.046, 4.0, 12.0, 15.0, 29.0, 31.0 })
            {
                data.Add(rate, days);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public void The_pool_query_selects_exactly_the_cards_the_scorer_calls_due(double rate, double days)
    {
        var card = Card(1, rate, days);

        var pool = VisitCandidatePool
            .DueByBurnWindow(new[] { card }.AsQueryable(), Now, Options)
            .Any();

        var scored = VisitPriority.Score(
            new CardVisitState
            {
                LastVisitedAt = card.LastVisitedAt,
                ObservedSalesPerDay = card.ObservedSalesPerDay,
            },
            Now,
            Options) >= BurnDueTier;

        Assert.True(
            pool == scored,
            $"rate {rate}/day at {days}d: pool says {pool}, scorer says {scored}");
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
    }
}
