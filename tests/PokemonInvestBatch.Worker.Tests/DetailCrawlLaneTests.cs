using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Domain.Tests.Fixtures;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.TestSupport;

namespace PokemonInvestBatch.Worker.Tests;

/// <summary>
/// One whole errand, end to end: a real page through the real parser into a
/// real database. Every other test in the suite proves a decision in isolation;
/// these prove the wiring between them, which is where the bugs that actually
/// reached production lived.
/// </summary>
public class DetailCrawlLaneTests : DatabaseTest, IDisposable
{
    private const string CardUrl = "/game/pokemon-base-set/charizard-4";

    private const long CardId = 630417;

    private readonly string _fingerprintDirectory =
        Path.Combine(Path.GetTempPath(), $"fingerprints-{Guid.NewGuid():N}");

    private LaneHarness NewHarness() => new(ContextOptions(), _fingerprintDirectory);

    private async Task SeedCardAsync(Action<Card>? adjust = null)
    {
        await using var db = NewContext();
        var now = DateTimeOffset.UtcNow;
        db.Sets.Add(new CardSet
        {
            Id = 1,
            Slug = "pokemon-base-set",
            Name = "Pokemon Base Set",
            DiscoveredAt = now,
            LastSeenAt = now,
        });

        var card = new Card
        {
            Id = CardId,
            SetId = 1,
            Url = CardUrl,
            Name = "Charizard #4",
            FirstSeenAt = now,
            LastSeenAt = now,
        };
        adjust?.Invoke(card);
        db.Cards.Add(card);
        await db.SaveChangesAsync();
    }

    [SkippableFact]
    public async Task A_good_page_becomes_history_and_a_clean_slate()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        // Seeded mid-quarantine: a success has to clear the record, not merely
        // add rows. That is the bench recheck's entire promise.
        await SeedCardAsync(c =>
        {
            c.FailureStreak = 4;
            c.QuarantinedUntil = DateTimeOffset.UtcNow.AddDays(2);
        });

        using var harness = NewHarness();
        var lane = harness.Build(new ScriptedHandler(ScriptedHandler.Page(Fixture.Load("charizard-live-a"))));
        await lane.CrawlOneAsync(CancellationToken.None);

        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CardId);
        Assert.Equal(0, card.FailureStreak);
        Assert.Null(card.QuarantinedUntil);
        Assert.NotNull(card.LastVisitedAt);

        Assert.NotEmpty(await db.PriceMonths.Where(p => p.CardId == CardId).ToListAsync());
        Assert.NotEmpty(await db.Sales.Where(s => s.CardId == CardId).ToListAsync());

        var visit = await db.Visits.SingleAsync(v => v.CardId == CardId);
        Assert.Equal(VisitOutcome.Parsed, visit.Outcome);
        Assert.Equal(200, visit.HttpStatus);
    }

    [SkippableFact]
    public async Task A_page_that_moved_earns_a_strike_and_writes_no_history()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await SeedCardAsync();

        using var harness = NewHarness();
        var lane = harness.Build(new ScriptedHandler(
            ScriptedHandler.Redirect("https://www.pricecharting.com/search-products?q=charizard")));
        await lane.CrawlOneAsync(CancellationToken.None);

        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CardId);
        Assert.Equal(1, card.FailureStreak);
        Assert.Null(card.QuarantinedUntil);
        Assert.Null(card.LastVisitedAt);

        // Nothing may be written from a page we never successfully read.
        Assert.Empty(await db.PriceMonths.ToListAsync());
        Assert.Empty(await db.Sales.ToListAsync());
        Assert.Equal(VisitOutcome.HttpError, (await db.Visits.SingleAsync()).Outcome);
    }

    [SkippableFact]
    public async Task The_third_strike_benches_the_card()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await SeedCardAsync();

        using var harness = NewHarness();
        var lane = harness.Build(new ScriptedHandler(
            ScriptedHandler.Redirect("https://www.pricecharting.com/search-products?q=charizard")));
        for (var i = 0; i < 3; i++)
        {
            await lane.CrawlOneAsync(CancellationToken.None);
        }

        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CardId);
        Assert.Equal(3, card.FailureStreak);
        Assert.NotNull(card.QuarantinedUntil);
        Assert.Contains(harness.Alerter.Raised, a => a.Subject == "Card quarantined");
    }

    [SkippableFact]
    public async Task A_broken_page_never_slows_the_crawl_for_everyone_else()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // The starvation incident, as a test. One deleted page used to trip the
        // site-in-trouble pause and then re-double the courtesy delay on every
        // retry, throttling the whole crawl from ~350 visits an hour to ~10 for
        // six hours. A card's own broken URL must cost the card, not the crawl.
        await SeedCardAsync();

        using var harness = NewHarness();
        var lane = harness.Build(new ScriptedHandler(
            ScriptedHandler.Redirect("https://www.pricecharting.com/search-products?q=charizard")));
        for (var i = 0; i < 3; i++)
        {
            await lane.CrawlOneAsync(CancellationToken.None);
        }

        Assert.False(harness.Delay.ShouldPause);
        Assert.Equal(TimeSpan.Zero, harness.Delay.Current);
        Assert.DoesNotContain(harness.Alerter.Raised, a => a.Subject == "Detail crawl paused");
    }

    [SkippableFact]
    public async Task Re_reading_an_unchanged_page_appends_no_new_history()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // Change-only history, proven end to end: the second visit sees the
        // same numbers and must add a price row for none of them.
        await SeedCardAsync();

        using var harness = NewHarness();
        var lane = harness.Build(new ScriptedHandler(ScriptedHandler.Page(Fixture.Load("charizard-live-a"))));
        await lane.CrawlOneAsync(CancellationToken.None);

        int afterOne;
        await using (var first = NewContext())
        {
            afterOne = await first.PriceMonths.CountAsync(p => p.CardId == CardId);
        }

        Assert.True(afterOne > 0);
        await lane.CrawlOneAsync(CancellationToken.None);

        await using var second = NewContext();
        Assert.Equal(afterOne, await second.PriceMonths.CountAsync(p => p.CardId == CardId));
        Assert.Equal(2, await second.Visits.CountAsync(v => v.CardId == CardId));
    }

    public void Dispose()
    {
        if (Directory.Exists(_fingerprintDirectory))
        {
            Directory.Delete(_fingerprintDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
