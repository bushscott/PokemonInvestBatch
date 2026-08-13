using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Domain.Tests.Fixtures;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.TestSupport;
using PokemonInvestBatch.Worker.Lanes;

namespace PokemonInvestBatch.Worker.Tests;

/// <summary>
/// The probe lane's first behavior tests, arriving with its second job. The
/// hand-delisted population keeps its old contract to the letter: raw fetch,
/// stamp, alert on 200, change nothing — the operator's verdict is the
/// operator's. The gone population is the machine's own, so the probe runs
/// the FULL visit errand and a 200 un-retires the card with fresh data in
/// the same transaction; a 302 just re-stamps the clock, builds no streak,
/// and never re-litigates the verdict with another set walk.
/// </summary>
public class DelistedProbeLaneTests : DatabaseTest, IDisposable
{
    private const long CardId = 630417;

    private const string CardUrl = "/game/pokemon-base-set/charizard-4";

    private readonly string _fingerprintDirectory =
        Path.Combine(Path.GetTempPath(), $"fingerprints-{Guid.NewGuid():N}");

    private LaneHarness NewHarness() => new(ContextOptions(), _fingerprintDirectory);

    private async Task SeedAsync(Action<Card> adjust, long id = CardId, string? url = null)
    {
        await using var db = NewContext();
        var now = DateTimeOffset.UtcNow;
        if (!await db.Sets.AnyAsync(s => s.Id == 1))
        {
            db.Sets.Add(new CardSet
            {
                Id = 1,
                Slug = "pokemon-base-set",
                Name = "Pokemon Base Set",
                DiscoveredAt = now,
                LastSeenAt = now,
            });
        }

        var card = new Card
        {
            Id = id,
            SetId = 1,
            Url = url ?? CardUrl,
            Name = $"Card {id}",
            FirstSeenAt = now.AddDays(-60),
            LastSeenAt = now.AddDays(-40),
        };
        adjust(card);
        db.Cards.Add(card);
        await db.SaveChangesAsync();
    }

    [SkippableFact]
    public async Task A_hand_delisted_card_still_gone_is_stamped_and_stays_quiet()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await SeedAsync(c => c.DelistedAt = DateTimeOffset.UtcNow.AddDays(-90));

        using var harness = NewHarness();
        var lane = harness.BuildProbeLane(new ScriptedHandler(
            ScriptedHandler.Redirect("/search-products?q=charizard")));
        await lane.ProbeDueAsync(CancellationToken.None);

        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CardId);
        Assert.NotNull(card.DelistedProbedAt);
        Assert.NotNull(card.DelistedAt);
        Assert.Empty(harness.Alerter.Raised);
    }

    [SkippableFact]
    public async Task A_hand_delisted_card_answering_200_alerts_and_changes_nothing()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await SeedAsync(c => c.DelistedAt = DateTimeOffset.UtcNow.AddDays(-90));

        using var harness = NewHarness();
        var lane = harness.BuildProbeLane(new ScriptedHandler(
            ScriptedHandler.Page(Fixture.Load("charizard-live-a"))));
        await lane.ProbeDueAsync(CancellationToken.None);

        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CardId);
        Assert.NotNull(card.DelistedAt);           // only the operator un-delists
        Assert.Null(card.LastVisitedAt);           // and the page was not parsed
        Assert.Equal(0, await db.Sales.CountAsync());
        Assert.Contains(harness.Alerter.Raised, a => a.Subject == "Delisted card is alive again");
    }

    [SkippableFact]
    public async Task A_gone_card_still_gone_restamps_without_a_strike_or_a_walk()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await SeedAsync(c => c.GoneAt = DateTimeOffset.UtcNow.AddDays(-2));

        using var harness = NewHarness();
        var handler = new ScriptedHandler(
            ScriptedHandler.Redirect("/search-products?q=charizard"));
        var lane = harness.BuildProbeLane(handler);
        await lane.ProbeDueAsync(CancellationToken.None);

        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CardId);
        Assert.NotNull(card.GoneAt);
        Assert.NotNull(card.DelistedProbedAt);
        Assert.Equal(0, card.FailureStreak);       // a probe is not a strike
        Assert.Null(card.QuarantinedUntil);
        Assert.Equal(1, handler.Calls);            // and never a second set walk
        Assert.Empty(harness.Alerter.Raised);
    }

    [SkippableFact]
    public async Task A_gone_card_answering_200_returns_with_its_data_in_one_visit()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await SeedAsync(c => c.GoneAt = DateTimeOffset.UtcNow.AddDays(-2));

        using var harness = NewHarness();
        var lane = harness.BuildProbeLane(new ScriptedHandler(
            ScriptedHandler.Page(Fixture.Load("charizard-live-a"))));
        await lane.ProbeDueAsync(CancellationToken.None);

        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CardId);
        Assert.Null(card.GoneAt);                  // machine verdict, machine-reversed
        Assert.NotNull(card.LastVisitedAt);        // by a full visit, not a peek
        Assert.True(await db.Sales.AnyAsync());
        Assert.Empty(harness.Alerter.Raised);      // a comeback is a log line, not an email
    }

    [SkippableFact]
    public async Task A_fresh_gone_verdict_waits_a_day_before_its_first_probe()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await SeedAsync(c => c.GoneAt = DateTimeOffset.UtcNow.AddHours(-2));

        using var harness = NewHarness();
        var handler = new ScriptedHandler(
            ScriptedHandler.Redirect("/search-products?q=charizard"));
        var lane = harness.BuildProbeLane(handler);
        await lane.ProbeDueAsync(CancellationToken.None);

        Assert.Equal(0, handler.Calls);
    }

    [SkippableFact]
    public async Task The_gone_probe_backs_off_by_doubling_the_silence()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        var now = DateTimeOffset.UtcNow;

        // Probed one day into retirement, one day ago: the silence equals the
        // gap, so it is due. Its twin was probed only an hour ago: not due.
        await SeedAsync(
            c =>
            {
                c.GoneAt = now.AddDays(-2);
                c.DelistedProbedAt = now.AddDays(-1);
            });
        await SeedAsync(
            c =>
            {
                c.GoneAt = now.AddDays(-2);
                c.DelistedProbedAt = now.AddHours(-1);
            },
            id: CardId + 1,
            url: "/game/pokemon-base-set/blastoise-2");

        using var harness = NewHarness();
        var handler = new ScriptedHandler(
            ScriptedHandler.Redirect("/search-products?q=charizard"));
        var lane = harness.BuildProbeLane(handler);
        await lane.ProbeDueAsync(CancellationToken.None);

        Assert.Equal(1, handler.Calls);

        await using var db = NewContext();
        var twin = await db.Cards.SingleAsync(c => c.Id == CardId + 1);
        Assert.Equal(now.AddHours(-1), twin.DelistedProbedAt!.Value, TimeSpan.FromSeconds(1));
    }

    [SkippableFact]
    public async Task One_sweep_drains_every_due_card_not_one()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        var stale = DateTimeOffset.UtcNow.AddDays(-90);
        await SeedAsync(c => c.DelistedAt = stale);
        await SeedAsync(c => c.DelistedAt = stale, id: CardId + 1, url: "/game/pokemon-base-set/blastoise-2");
        await SeedAsync(c => c.GoneAt = DateTimeOffset.UtcNow.AddDays(-2), id: CardId + 2, url: "/game/pokemon-base-set/venusaur-15");

        using var harness = NewHarness();
        var handler = new ScriptedHandler(
            ScriptedHandler.Redirect("/search-products?q=x"));
        var lane = harness.BuildProbeLane(handler);
        await lane.ProbeDueAsync(CancellationToken.None);

        Assert.Equal(3, handler.Calls);

        await using var db = NewContext();
        Assert.Equal(3, await db.Cards.CountAsync(c => c.DelistedProbedAt != null));
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
