using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Application.Scheduling;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Integration.Tests;

/// <summary>
/// Proves the burn-window candidate query is translatable by the Npgsql
/// provider. ToQueryString renders SQL without a connection, so this runs
/// everywhere — an untranslatable expression would otherwise surface as a
/// runtime crash in the detail lane on the Pi.
/// </summary>
public class BurnWindowQueryTranslationTests
{
    [Fact]
    public void Burn_window_due_candidate_query_translates_to_sql()
    {
        var options = new DbContextOptionsBuilder<PokemonDbContext>()
            .UseNpgsql("Host=localhost;Database=translation-check-only")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new PokemonDbContext(options);
        var now = new DateTimeOffset(2026, 7, 29, 0, 0, 0, TimeSpan.Zero);

        var sql = VisitCandidatePool
            .DueByBurnWindow(VisitCandidatePool.Eligible(db, now), now, new VisitPriorityOptions())
            .ToQueryString();

        Assert.Contains("SELECT", sql);
    }
}
