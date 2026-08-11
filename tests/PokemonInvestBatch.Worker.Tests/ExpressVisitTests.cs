using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using PokemonInvestBatch.Domain.Tests.Fixtures;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.TestSupport;
using PokemonInvestBatch.Worker.Intake;

namespace PokemonInvestBatch.Worker.Tests;

/// <summary>
/// The instantaneous path, end to end: the same errand as the lane, minus the
/// pick and the gate, while a caller waits on the answer. These pin the two
/// promises express makes — one visit implementation (history, strikes, and
/// the shared backoff behave identically) and no waiting: different cards fetch
/// at the same time, while concurrent asks for one card share a single fetch.
/// </summary>
public class ExpressVisitTests : DatabaseTest, IDisposable
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
    public async Task An_express_visit_persists_history_while_the_caller_waits()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // Seeded with a pending ask: a successful express visit is a
        // successful visit, so it satisfies the queued request too.
        await SeedCardAsync(c => c.RefreshRequestedAt = DateTimeOffset.UtcNow);

        using var harness = NewHarness();
        var runner = harness.BuildExpressRunner(
            new ScriptedHandler(ScriptedHandler.Page(Fixture.Load("charizard-live-a"))));

        var result = await runner.RunAsync(CardId, CancellationToken.None);

        var completed = Assert.IsType<ExpressCompleted>(result);
        Assert.Equal(VisitOutcome.Parsed, completed.Visit.Outcome);
        Assert.False(completed.Coalesced);

        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CardId);
        Assert.NotNull(card.LastVisitedAt);
        Assert.Null(card.RefreshRequestedAt);
        Assert.NotEmpty(await db.PriceMonths.Where(p => p.CardId == CardId).ToListAsync());
        Assert.Equal(VisitOutcome.Parsed, (await db.Visits.SingleAsync(v => v.CardId == CardId)).Outcome);
    }

    [SkippableFact]
    public async Task An_express_visit_for_an_unknown_card_reports_not_found()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        using var harness = NewHarness();
        var runner = harness.BuildExpressRunner(
            new ScriptedHandler(ScriptedHandler.Page(Fixture.Load("charizard-live-a"))));

        var result = await runner.RunAsync(999_999, CancellationToken.None);

        Assert.IsType<ExpressUnknownCard>(result);
    }

    [SkippableFact]
    public async Task An_express_failure_earns_a_strike_like_any_visit()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // One truth for card health: the shared pipeline records the strike
        // whichever path delivered the visit, and writes no history from a
        // page it never read.
        await SeedCardAsync(c => c.RefreshRequestedAt = DateTimeOffset.UtcNow);

        using var harness = NewHarness();
        var runner = harness.BuildExpressRunner(new ScriptedHandler(
            ScriptedHandler.Redirect("https://www.pricecharting.com/search-products?q=charizard")));

        var result = await runner.RunAsync(CardId, CancellationToken.None);

        var completed = Assert.IsType<ExpressCompleted>(result);
        Assert.Equal(VisitOutcome.HttpError, completed.Visit.Outcome);

        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CardId);
        Assert.Equal(1, card.FailureStreak);

        // A failed visit leaves the queued ask standing, express or not.
        Assert.NotNull(card.RefreshRequestedAt);
        Assert.Empty(await db.PriceMonths.ToListAsync());
    }

    [SkippableFact]
    public async Task Express_site_trouble_teaches_the_shared_backoff()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // Express skips the gate, not the lesson: a 500 on the express path
        // counts toward the same three-strike pause the lanes obey.
        await SeedCardAsync();

        using var harness = NewHarness();
        var runner = harness.BuildExpressRunner(new ScriptedHandler(
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        await runner.RunAsync(CardId, CancellationToken.None);

        Assert.Equal(1, harness.Delay.ConsecutiveFailures);
    }

    [SkippableFact]
    public async Task Concurrent_express_requests_for_the_same_card_share_one_visit()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // A double-clicked refresh button must not cost the site two fetches:
        // the second caller coalesces onto the in-flight visit and hears the
        // same answer.
        await SeedCardAsync();

        using var harness = NewHarness();
        var gated = new GatedHandler(Fixture.Load("charizard-live-a"));
        var runner = harness.BuildExpressRunner(gated);

        var first = runner.RunAsync(CardId, CancellationToken.None);
        await WaitUntilAsync(() => gated.Calls == 1);

        var second = runner.RunAsync(CardId, CancellationToken.None);
        Assert.False(second.IsCompleted);

        gated.Release();
        var firstResult = Assert.IsType<ExpressCompleted>(await first);
        var secondResult = Assert.IsType<ExpressCompleted>(await second);

        Assert.Equal(1, gated.Calls);
        Assert.False(firstResult.Coalesced);
        Assert.True(secondResult.Coalesced);
        Assert.Equal(VisitOutcome.Parsed, secondResult.Visit.Outcome);

        await using var db = NewContext();
        Assert.Equal(1, await db.Visits.CountAsync(v => v.CardId == CardId));
    }

    [SkippableFact]
    public async Task Express_visits_for_different_cards_run_at_the_same_time()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // No floor and no queue: every express call is a person waiting on a
        // page, so a second card fetches immediately instead of waiting out
        // the first (ADR-0008). Second card, same runner.
        await SeedCardAsync();
        await using (var db = NewContext())
        {
            db.Cards.Add(new Card
            {
                Id = 111,
                SetId = 1,
                Url = "/game/pokemon-base-set/blastoise-2",
                Name = "Blastoise #2",
                FirstSeenAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // The handler holds every response open, so both fetches can only be
        // at the site together if neither is waiting on the other. The clock
        // is never advanced either: a wait keyed to time would park the second
        // visit forever rather than merely slow it.
        //
        // Holding both fetches also puts both visits past their fingerprint
        // read before either writes, so this doubles as the reproduction for
        // the archive race: before PageFingerprintArchive claimed the row with
        // an upsert, the loser died on pk_fingerprints and its whole visit
        // rolled back — a 500 the caller had done nothing to earn.
        using var harness = NewHarness();
        var gated = new GatedHandler(Fixture.Load("charizard-live-a"));
        var runner = harness.BuildExpressRunner(gated, new FakeTimeProvider());

        var first = runner.RunAsync(CardId, CancellationToken.None);
        var second = runner.RunAsync(111, CancellationToken.None);
        await WaitUntilAsync(() => gated.Calls == 2);

        gated.Release();
        Assert.IsType<ExpressCompleted>(await first.WaitAsync(TimeSpan.FromSeconds(30)));
        var secondResult = Assert.IsType<ExpressCompleted>(await second.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(secondResult.Coalesced);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition never became true");
            await Task.Delay(10);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_fingerprintDirectory))
        {
            Directory.Delete(_fingerprintDirectory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Counts calls and holds every response until released, so a
    /// test can prove two callers shared one fetch.</summary>
    private sealed class GatedHandler(string html) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _calls;

        public int Calls => _calls;

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            await _release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html) };
        }
    }
}
