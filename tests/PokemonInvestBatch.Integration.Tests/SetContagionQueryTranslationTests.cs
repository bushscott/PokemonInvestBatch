using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Integration.Tests;

/// <summary>
/// Proves the set-contagion sibling query is translatable by the Npgsql
/// provider. ToQueryString renders SQL without a connection, so this runs
/// everywhere — an untranslatable expression would otherwise surface as a
/// runtime crash the first time a bucket caps in production.
/// </summary>
public class SetContagionQueryTranslationTests
{
    [Fact]
    public void Hottest_set_siblings_query_translates_to_sql()
    {
        var options = new DbContextOptionsBuilder<PokemonDbContext>()
            .UseNpgsql("Host=localhost;Database=translation-check-only")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new PokemonDbContext(options);

        var sql = VisitCandidatePool
            .HottestSetSiblings(db, setId: 7, exceptCardId: 42)
            .ToQueryString();

        Assert.Contains("SELECT", sql);
        Assert.Contains("observed_sales_per_day", sql);
        Assert.Contains("refresh_requested_at", sql);
        Assert.Contains("LIMIT", sql);
    }
}
