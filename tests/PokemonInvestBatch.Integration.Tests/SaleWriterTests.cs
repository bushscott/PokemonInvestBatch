using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Domain.Parsing;
using PokemonInvestBatch.Domain.Tests.Fixtures;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.TestSupport;

namespace PokemonInvestBatch.Integration.Tests;

/// <summary>
/// Dedup and hostile-input handling, against real PostgreSQL. Each test builds
/// and drops its own database; see DatabaseTest.
/// </summary>
public class SaleWriterTests : DatabaseTest
{

    [SkippableFact]
    public async Task Appending_the_same_page_twice_inserts_nothing_new()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        await using var db = NewContext();
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
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        await using var db = NewContext();
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
