using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Integration.Tests;

/// <summary>
/// Proves the stats sweep's at-risk query is translatable by the Npgsql
/// provider. ToQueryString renders SQL without a connection, so this runs
/// everywhere — an untranslatable expression would otherwise surface as a
/// runtime crash in the stats lane on the Pi.
/// </summary>
public class AtRiskQueryTranslationTests
{
    [Fact]
    public void Past_burn_fraction_query_translates_to_sql()
    {
        var options = new DbContextOptionsBuilder<PokemonDbContext>()
            .UseNpgsql("Host=localhost;Database=translation-check-only")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new PokemonDbContext(options);
        var now = new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero);

        var sql = VisitCandidatePool.PastBurnFraction(db.Cards, now, 0.75).ToQueryString();

        Assert.Contains("SELECT", sql);

        // Retired non-cards and delisted cards will never be visited again, so
        // counting them at-risk would ratchet into a permanent false alarm.
        Assert.Contains("delisted_at", sql);
        Assert.Contains("not_a_card_at", sql);
        Assert.Contains("gone_at", sql);
    }
}
