using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Domain.Tests.Fixtures;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.TestSupport;
using PokemonInvestBatch.Worker.Lanes;

namespace PokemonInvestBatch.Worker.Tests;

/// <summary>
/// The cataloging walk's first tests, written as it moved out of
/// EnumerationLane (which had none) into a component the gone-verdict path can
/// also call. Characterization over the real base-set listing fixtures: page
/// through, upsert, heal by product id, and tell a completed walk from an
/// abandoned one — because "the walk completed and the card was not there" is
/// about to become evidence that retires cards.
/// </summary>
public class SetWalkerTests : DatabaseTest
{
    private const long SetId = 1;

    /// <summary>Charizard #4's product id on page 1 of the fixture.</summary>
    private const long CharizardId = 630417;

    /// <summary>The fixture pages all offer a next-page form; a walk that ends
    /// needs a last page, made by disarming the form marker the parser looks
    /// for (form.js-next-page).</summary>
    private static string LastPage(string fixture) =>
        Fixture.Load(fixture).Replace("js-next-page", "js-next-page-disarmed");

    private async Task SeedSetAsync(Action<PokemonDbContext>? more = null)
    {
        await using var db = NewContext();
        var now = DateTimeOffset.UtcNow;
        db.Sets.Add(new CardSet
        {
            Id = SetId,
            Slug = "pokemon-base-set",
            Name = "Pokemon Base Set",
            DiscoveredAt = now,
            LastSeenAt = now,
        });
        more?.Invoke(db);
        await db.SaveChangesAsync();
    }

    [SkippableFact]
    public async Task A_completed_walk_catalogs_every_product_and_stamps_the_set()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await SeedSetAsync();

        using var harness = new LaneHarness(ContextOptions(), Path.GetTempPath());
        var walker = harness.BuildWalker(new ScriptedHandler(
            ScriptedHandler.Page(Fixture.Load("console-base-set-page1")),
            ScriptedHandler.Page(Fixture.Load("console-base-set-page2")),
            ScriptedHandler.Page(LastPage("console-base-set-page3"))));

        var result = await walker.WalkSetAsync(SetId, CancellationToken.None);

        Assert.True(result.Completed);
        Assert.Equal(450, result.CardsSeen);

        await using var db = NewContext();
        Assert.NotNull((await db.Sets.SingleAsync(s => s.Id == SetId)).LastWalkedAt);
        var charizard = await db.Cards.SingleAsync(c => c.Id == CharizardId);
        Assert.Equal("/game/pokemon-base-set/charizard-4", charizard.Url);
    }

    [SkippableFact]
    public async Task A_failed_page_leaves_the_walk_incomplete_but_keeps_what_it_saw()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await SeedSetAsync();

        using var harness = new LaneHarness(ContextOptions(), Path.GetTempPath());
        var walker = harness.BuildWalker(new ScriptedHandler(
            ScriptedHandler.Page(Fixture.Load("console-base-set-page1")),
            ScriptedHandler.Redirect("/search-products?q=whoops")));

        var result = await walker.WalkSetAsync(SetId, CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Equal(150, result.CardsSeen);

        await using var db = NewContext();
        // Page 1's upsert is already committed — resumability, not atomicity —
        // but only a completed cursor walk stamps the set.
        Assert.Null((await db.Sets.SingleAsync(s => s.Id == SetId)).LastWalkedAt);
        Assert.Equal(150, await db.Cards.CountAsync());
    }

    [SkippableFact]
    public async Task A_known_card_is_healed_by_product_id_even_while_tombstoned()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        var stale = DateTimeOffset.UtcNow.AddDays(-30);
        await SeedSetAsync(db =>
        {
            db.Sets.Add(new CardSet
            {
                Id = 2,
                Slug = "pokemon-wrong-set",
                Name = "Wrong Set",
                DiscoveredAt = stale,
                LastSeenAt = stale,
            });
            // Charizard filed under the wrong set at a dead URL, and hand-
            // retired: the walk must still heal the row — the log-only branch
            // for tombstones is deliberate, the heal is unconditional.
            db.Cards.Add(new Card
            {
                Id = CharizardId,
                SetId = 2,
                Url = "/game/pokemon-wrong-set/charizard-old",
                Name = "Charizard (stale)",
                FirstSeenAt = stale,
                LastSeenAt = stale,
                DelistedAt = stale,
            });
        });

        using var harness = new LaneHarness(ContextOptions(), Path.GetTempPath());
        var walker = harness.BuildWalker(new ScriptedHandler(
            ScriptedHandler.Page(LastPage("console-base-set-page1"))));

        var result = await walker.WalkSetAsync(SetId, CancellationToken.None);
        Assert.True(result.Completed);

        await using var db = NewContext();
        var healed = await db.Cards.SingleAsync(c => c.Id == CharizardId);
        Assert.Equal("/game/pokemon-base-set/charizard-4", healed.Url);
        Assert.Equal(SetId, healed.SetId);
        Assert.True(healed.LastSeenAt > stale);
        Assert.NotNull(healed.DelistedAt); // healing never reverses a human verdict
    }

    [SkippableFact]
    public async Task A_runaway_pagination_abandons_loudly()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await SeedSetAsync();

        using var harness = new LaneHarness(ContextOptions(), Path.GetTempPath());
        // Every page offers a next form; the guard must abandon at the cap.
        var walker = harness.BuildWalker(
            new ScriptedHandler(ScriptedHandler.Page(Fixture.Load("console-base-set-page1"))),
            new ScraperOptions { ContactEmail = "tests@example.com", MaxSetWalkPages = 2 });

        var result = await walker.WalkSetAsync(SetId, CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Contains(harness.Alerter.Raised, a => a.Subject.StartsWith("Set walk runaway"));

        await using var db = NewContext();
        Assert.Null((await db.Sets.SingleAsync(s => s.Id == SetId)).LastWalkedAt);
    }
}
