using Microsoft.EntityFrameworkCore;
using Npgsql;
using PokemonInvestBatch.Domain.Parsing;
using PokemonInvestBatch.Domain.Tests.Fixtures;
using PokemonInvestBatch.Infrastructure.Persistence;
using Respawn;

namespace PokemonInvestBatch.Integration.Tests;

/// <summary>
/// Runs against pokemon_test on the Pi. Set POKEMON_TEST_DB to e.g.
/// "Host=&lt;pi-ip&gt;;Database=pokemon_test;Username=pokemon_tester;Password=..."
/// — tests skip when it is not set.
/// </summary>
public class SaleWriterTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("POKEMON_TEST_DB");

    [SkippableFact]
    public async Task Appending_the_same_page_twice_inserts_nothing_new()
    {
        Skip.If(ConnectionString is null, "POKEMON_TEST_DB not set (needs the Pi's pokemon_test database).");

        await using var db = CreateContext();
        await db.Database.MigrateAsync(CancellationToken.None);
        await ResetAsync();
        await SeedCharizardAsync(db);
        var writer = new SaleWriter(db);

        // The two live fixtures are byte-identical fetches of the same page —
        // the real-world "batch runs over and over" case.
        var first = CardDetailParser.Parse(Fixture.Load("charizard-live-a"));
        var inserted = await writer.AppendNewAsync(
            630417, first.Sales, DateTimeOffset.UtcNow, CancellationToken.None);

        var second = CardDetailParser.Parse(Fixture.Load("charizard-live-b"));
        var reinserted = await writer.AppendNewAsync(
            630417, second.Sales, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(410, inserted);
        Assert.Equal(0, reinserted);
        Assert.Equal(410, await db.Sales.CountAsync(CancellationToken.None));
    }

    [SkippableFact]
    public async Task Hostile_titles_are_stored_verbatim_not_executed()
    {
        Skip.If(ConnectionString is null, "POKEMON_TEST_DB not set (needs the Pi's pokemon_test database).");

        await using var db = CreateContext();
        await db.Database.MigrateAsync(CancellationToken.None);
        await ResetAsync();
        await SeedCharizardAsync(db);

        const string hostile = "'); DROP TABLE sales;--";
        var sale = new SaleRecord
        {
            Source = "ebay",
            SourceId = "hostile-1",
            SoldOn = new DateOnly(2026, 7, 1),
            GradeTier = "Ungraded",
            PriceCents = 100,
            Title = hostile,
        };

        await new SaleWriter(db).AppendNewAsync(
            630417, [sale], DateTimeOffset.UtcNow, CancellationToken.None);

        var stored = await db.Sales.SingleAsync(CancellationToken.None);
        Assert.Equal(hostile, stored.Title);
    }

    private static PokemonDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<PokemonDbContext>()
            .UseNpgsql(ConnectionString!)
            .UseSnakeCaseNamingConvention()
            .Options);

    private static async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString!);
        await connection.OpenAsync();
        var respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
        });
        await respawner.ResetAsync(connection);
    }

    private static async Task SeedCharizardAsync(PokemonDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        db.Sets.Add(new CardSet
        {
            Id = 1,
            Slug = "pokemon-base-set",
            Name = "Pokemon Base Set",
            DiscoveredAt = now,
            LastSeenAt = now,
        });
        db.Cards.Add(new Card
        {
            Id = 630417,
            SetId = 1,
            Url = "/game/pokemon-base-set/charizard-4",
            Name = "Charizard #4",
            FirstSeenAt = now,
            LastSeenAt = now,
        });
        await db.SaveChangesAsync();
    }
}
