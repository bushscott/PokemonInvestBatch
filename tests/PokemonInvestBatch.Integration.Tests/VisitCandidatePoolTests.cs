using Microsoft.EntityFrameworkCore;
using Npgsql;
using PokemonInvestBatch.Application.Scheduling;
using PokemonInvestBatch.Infrastructure.Persistence;
using Respawn;

namespace PokemonInvestBatch.Integration.Tests;

/// <summary>
/// Runs against pokemon_test on the Pi (see SaleWriterTests for the
/// POKEMON_TEST_DB convention). Closes the stress tests' blind spot: they
/// score the full corpus, so they can never notice the candidate pool
/// itself excluding a card the scorer would have picked.
/// </summary>
public class VisitCandidatePoolTests
{
    private static string? ConnectionString => Environment.GetEnvironmentVariable("POKEMON_TEST_DB");

    [SkippableFact]
    public async Task A_hot_card_reaches_the_scorer_despite_being_far_from_the_stalest_window()
    {
        Skip.If(ConnectionString is null, "POKEMON_TEST_DB not set (needs the Pi's pokemon_test database).");

        await using var db = CreateContext();
        await db.Database.MigrateAsync(CancellationToken.None);
        await ResetAsync();
        var now = DateTimeOffset.UtcNow;

        // 2,000 cold cards, all far staler than the hot card — enough that a
        // staleness-ordered Take(500) can never reach it.
        db.Sets.Add(new CardSet
        {
            Id = 1,
            Slug = "pokemon-base-set",
            Name = "Pokemon Base Set",
            DiscoveredAt = now,
            LastSeenAt = now,
        });
        for (var i = 1; i <= 2_000; i++)
        {
            db.Cards.Add(new Card
            {
                Id = i,
                SetId = 1,
                Url = $"/game/pokemon-base-set/cold-{i}",
                Name = $"Cold #{i}",
                FirstSeenAt = now,
                LastSeenAt = now,
                LastVisitedAt = now.AddDays(-20),
            });
        }

        // Selling 6/day, visited 3 days ago: 18 sales-worth of staleness has
        // consumed over half the 30-row bucket — burn-window due right now.
        var hot = new Card
        {
            Id = 9_999,
            SetId = 1,
            Url = "/game/pokemon-base-set/hot-9999",
            Name = "Hot #9999",
            FirstSeenAt = now,
            LastSeenAt = now,
            LastVisitedAt = now.AddDays(-3),
            ObservedSalesPerDay = 6,
        };
        db.Cards.Add(hot);
        await db.SaveChangesAsync();

        var priorityOptions = new VisitPriorityOptions();
        var pool = await VisitCandidatePool.LoadAsync(db, now, priorityOptions, CancellationToken.None);

        Assert.Contains(pool, c => c.Id == hot.Id);

        var winner = pool.MaxBy(c => VisitPriority.Score(
            new CardVisitState
            {
                LastVisitedAt = c.LastVisitedAt,
                ObservedSalesPerDay = c.ObservedSalesPerDay,
                AnyBucketAtCap = c.AnyBucketAtCap,
            },
            now,
            priorityOptions));
        Assert.Equal(hot.Id, winner!.Id);
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
            // Respawn must never erase applied-migration bookkeeping, or the
            // next MigrateAsync re-runs InitialCreate against existing tables.
            TablesToIgnore = [new Respawn.Graph.Table("__EFMigrationsHistory")],
        });
        await respawner.ResetAsync(connection);
    }
}
