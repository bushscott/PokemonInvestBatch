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

    [SkippableFact]
    public async Task A_console_page_is_retired_instead_of_benched()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // The pokemon-mini incident, as a test. A handheld console page parses
        // perfectly as a card — same markup, same chart series — and wrote 421
        // months of console prices under grade-tier names before anyone noticed.
        // Seeded mid-sentence, because the point is that the verdict ends the
        // retry loop rather than joining it.
        await SeedCardAsync(c =>
        {
            c.FailureStreak = 3;
            c.QuarantinedUntil = DateTimeOffset.UtcNow.AddDays(1);
        });

        using var harness = NewHarness();
        var lane = harness.Build(new ScriptedHandler(
            ScriptedHandler.Page(Fixture.Load("pokemon-mini-pinball"))));
        await lane.CrawlOneAsync(CancellationToken.None);

        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CardId);
        Assert.NotNull(card.NotACardAt);

        // Cleared on the way out: they described a card that kept failing, and
        // leaving them set would keep it counted among the benched forever.
        Assert.Equal(0, card.FailureStreak);
        Assert.Null(card.QuarantinedUntil);

        // Not one row of console history may reach the corpus.
        Assert.Empty(await db.PriceMonths.ToListAsync());
        Assert.Empty(await db.Sales.ToListAsync());

        // One alert, naming the set — that is the thing you act on.
        var alert = Assert.Single(harness.Alerter.Raised, a => a.Subject == "A set in the catalog is not cards");
        Assert.Contains("pokemon-base-set", alert.Body);
        Assert.Contains("blacklist.json", alert.Body);
    }

    [SkippableFact]
    public async Task A_console_page_leaves_the_rotation_for_good()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // The flatline this whole change exists to end: a benched card returns
        // every ten minutes forever. A retired one must never be picked again,
        // by the ordinary pool or by the bench recheck.
        await SeedCardAsync();

        using var harness = NewHarness();
        var lane = harness.Build(new ScriptedHandler(
            ScriptedHandler.Page(Fixture.Load("pokemon-mini-pinball"))));
        await lane.CrawlOneAsync(CancellationToken.None);

        await using var db = NewContext();
        var now = DateTimeOffset.UtcNow;
        Assert.Empty(await VisitCandidatePool.Eligible(db, now).ToListAsync());
        Assert.Empty(await VisitCandidatePool.Benched(db, now).ToListAsync());

        // And the audit trail survives — this is the evidence for why.
        Assert.Equal(VisitOutcome.NotACard, (await db.Visits.SingleAsync()).Outcome);
        Assert.Single(await db.ParseFailures.ToListAsync());
    }

    [SkippableFact]
    public async Task Retirements_do_not_push_the_parse_failure_rate_into_a_spike()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // The spike detector reads the visits table, not the metric — so filing
        // retirements as ParseFailed would let a miscatalogued set raise "the
        // site changed and the parser is blind". Six in one hundred-visit window
        // clears the 5% threshold on its own.
        await SeedCardAsync();

        await using (var seed = NewContext())
        {
            var start = DateTimeOffset.UtcNow.AddHours(-2);
            for (var i = 0; i < 100; i++)
            {
                seed.Visits.Add(new PageVisit
                {
                    Kind = PageKind.CardDetail,
                    Url = CardUrl,
                    CardId = CardId,
                    FetchedAt = start.AddSeconds(i),
                    HttpStatus = 200,
                    // The six most recent are retirements, so they are certain to
                    // fall inside the window the detector samples.
                    Outcome = i >= 94 ? VisitOutcome.NotACard : VisitOutcome.Parsed,
                });
            }

            await seed.SaveChangesAsync();
        }

        // One genuine drift failure, which is what runs the check.
        using var harness = NewHarness();
        var lane = harness.Build(new ScriptedHandler(
            ScriptedHandler.Page(Fixture.Load("charizard-2024-06-pop-schema"))));
        await lane.CrawlOneAsync(CancellationToken.None);

        await using var db = NewContext();
        Assert.Equal(1, await db.Visits.CountAsync(v => v.Outcome == VisitOutcome.ParseFailed));
        Assert.Equal(6, await db.Visits.CountAsync(v => v.Outcome == VisitOutcome.NotACard));

        // 1 real failure in 100 is 1%. Counting the six retirements would make it
        // 7% and wake someone for a cataloging mistake.
        Assert.DoesNotContain(harness.Alerter.Raised, a => a.Subject == "Parse failure rate spike");
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
