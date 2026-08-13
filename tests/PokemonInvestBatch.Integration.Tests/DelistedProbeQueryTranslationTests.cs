using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Integration.Tests;

/// <summary>
/// Proves the delisted probe's pick translates to SQL. This one earns the
/// check twice over: it is the only query with a null-first ordering, and it
/// runs once every six hours on a lane whose whole job is to be quiet — an
/// untranslatable expression would fail silently for a month before anyone
/// noticed the probe had never run.
/// </summary>
public class DelistedProbeQueryTranslationTests
{
    [Fact]
    public void Delisted_probe_query_translates_to_sql()
    {
        var options = new DbContextOptionsBuilder<PokemonDbContext>()
            .UseNpgsql("Host=localhost;Database=translation-check-only")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new PokemonDbContext(options);
        var now = new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);

        var sql = VisitCandidatePool
            .DueForDelistedProbe(db, now, TimeSpan.FromDays(30))
            .ToQueryString();

        Assert.Contains("c.delisted_at IS NOT NULL", sql);
        Assert.Contains("c.delisted_probed_at IS NULL OR", sql);

        // The rotation's subtle half: Postgres sorts NULLs last by default,
        // which would park every never-probed card behind the whole bench.
        Assert.Contains("ORDER BY c.delisted_probed_at IS NOT NULL", sql);
        Assert.Contains("LIMIT", sql);
    }

    [Fact]
    public void Gone_probe_query_translates_to_sql()
    {
        // The doubling clock is pure timestamp arithmetic — silence since the
        // last probe vs the capped gap between retirement and that probe — and
        // interval subtraction inside a CASE is exactly the kind of expression
        // that runs fine as LINQ-to-Objects and throws on the Pi.
        var options = new DbContextOptionsBuilder<PokemonDbContext>()
            .UseNpgsql("Host=localhost;Database=translation-check-only")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var db = new PokemonDbContext(options);
        var now = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero);

        var sql = VisitCandidatePool.DueForGoneProbe(db, now).ToQueryString();

        Assert.Contains("gone_at IS NOT NULL", sql);
        Assert.Contains("delisted_probed_at", sql);
        Assert.Contains("CASE", sql);
        Assert.Contains("LIMIT", sql);
    }
}
