using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Application.Alerting;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.TestSupport;

namespace PokemonInvestBatch.Worker.Tests;

/// <summary>
/// The gone-verdict path: a card 302ing at the bench threshold asks its own
/// set's listing before accepting a sentence, and the listing's testimony —
/// renamed, removed, or still-listed — decides whether it heals, retires, or
/// benches. Every case here was a manual diagnosis first (Moltres 13766134,
/// Vaporeon 13971735, the Arceus heal); the assertions are those diagnoses,
/// mechanized. ADR-0010 holds the safeguards these tests pin: no verdict
/// from an incomplete or empty walk, and a corpus-wide breaker against mass
/// retirement (ADR-0002's objection, answered rather than ignored).
/// </summary>
public class GoneVerdictTests : DatabaseTest, IDisposable
{
    private const long CardId = 630417;

    private const long SetId = 1;

    private const string CardUrl = "/game/pokemon-base-set/charizard-4";

    private readonly string _fingerprintDirectory =
        Path.Combine(Path.GetTempPath(), $"fingerprints-{Guid.NewGuid():N}");

    private LaneHarness NewHarness() => new(ContextOptions(), _fingerprintDirectory);

    /// <summary>A minimal one-page listing (no next-page form) carrying the
    /// given products — the synthetic counterpart of the console fixtures,
    /// small enough to state a test's evidence inline.</summary>
    private static string Listing(params (long Id, string Url, string Name)[] products) =>
        "<html><body><table><tbody>"
        + string.Concat(products.Select(p =>
            $"<tr id=\"product-{p.Id}\" data-product=\"{p.Id}\">"
            + $"<td class=\"title\" title=\"{p.Id}\"><a href=\"{p.Url}\">{p.Name}</a></td></tr>"))
        + "</tbody></table></body></html>";

    private async Task SeedAsync(int failureStreak = 2, Action<PokemonDbContext>? more = null)
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
        db.Cards.Add(new Card
        {
            Id = CardId,
            SetId = SetId,
            Url = CardUrl,
            Name = "Charizard #4",
            FirstSeenAt = now.AddDays(-30),
            LastSeenAt = now.AddDays(-8),
            LastVisitedAt = now.AddDays(-1),
            FailureStreak = failureStreak,
        });
        more?.Invoke(db);
        await db.SaveChangesAsync();
    }

    private static ScriptedHandler CardRedirectThen(params Func<HttpResponseMessage>[] listingPages) =>
        new([
            ScriptedHandler.Redirect("https://www.pricecharting.com/search-products?q=charizard"),
            .. listingPages,
        ]);

    private async Task VisitAsync(LaneHarness harness)
    {
        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CardId);
        await harness.Visitor.VisitAsync(db, card, visit: null, "card pages", CancellationToken.None);
    }

    [SkippableFact]
    public async Task A_removed_card_is_retired_by_the_listing_not_the_bench()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // Two strikes down, the third 302 lands — and the set's listing no
        // longer carries the product id. Vaporeon's manual diagnosis, run by
        // the machine: retired quietly, nothing benched, nobody emailed.
        await SeedAsync(failureStreak: 2);

        using var harness = NewHarness();
        harness.Build(
            CardRedirectThen(ScriptedHandler.Page(Listing((999, "/game/pokemon-base-set/other-1", "Other #1")))),
            new IncidentThrottle(TimeSpan.Zero));
        await VisitAsync(harness);

        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CardId);
        Assert.NotNull(card.GoneAt);
        Assert.Null(card.QuarantinedUntil);
        Assert.Equal(0, card.FailureStreak);
        Assert.DoesNotContain(harness.Alerter.Raised, a => a.Subject == "Card quarantined");
    }

    [SkippableFact]
    public async Task A_renamed_card_is_healed_by_the_listing_same_day()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // The listing still carries the id — under a new slug. The walk's
        // by-product-id heal lands the new URL and the card resumes without a
        // bench: the Arceus recovery, without waiting for the weekly walk.
        await SeedAsync(failureStreak: 2);

        using var harness = NewHarness();
        harness.Build(
            CardRedirectThen(ScriptedHandler.Page(Listing(
                (CardId, "/game/pokemon-base-set/charizard-4-renamed", "Charizard #4")))),
            new IncidentThrottle(TimeSpan.Zero));
        await VisitAsync(harness);

        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CardId);
        Assert.Equal("/game/pokemon-base-set/charizard-4-renamed", card.Url);
        Assert.Null(card.GoneAt);
        Assert.Null(card.QuarantinedUntil);
        Assert.Equal(0, card.FailureStreak);
    }

    [SkippableFact]
    public async Task A_card_still_listed_at_its_dead_url_benches_as_before()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // The catalog insists the URL is right and the URL 302s anyway — the
        // phantom class. The listing proves nothing here, so the bench keeps
        // custody exactly as it did before this path existed.
        await SeedAsync(failureStreak: 2);

        using var harness = NewHarness();
        harness.Build(
            CardRedirectThen(ScriptedHandler.Page(Listing((CardId, CardUrl, "Charizard #4")))),
            new IncidentThrottle(TimeSpan.Zero));
        await VisitAsync(harness);

        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CardId);
        Assert.Null(card.GoneAt);
        Assert.NotNull(card.QuarantinedUntil);
        Assert.Equal(3, card.FailureStreak);
        Assert.Contains(harness.Alerter.Raised, a => a.Subject == "Card quarantined");
    }

    [SkippableFact]
    public async Task An_incomplete_walk_renders_no_verdict()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // The listing fetch itself fails: the walk proves nothing about any
        // card it did not reach, so the card benches as before. ADR-0010
        // brake: absence of evidence, not evidence of absence.
        await SeedAsync(failureStreak: 2);

        using var harness = NewHarness();
        harness.Build(
            CardRedirectThen(ScriptedHandler.Redirect("/search-products?q=pokemon-base-set")),
            new IncidentThrottle(TimeSpan.Zero));
        await VisitAsync(harness);

        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CardId);
        Assert.Null(card.GoneAt);
        Assert.NotNull(card.QuarantinedUntil);
    }

    [SkippableFact]
    public async Task An_empty_listing_renders_no_verdict()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // A completed walk of zero products is a site change wearing a
        // listing's clothes — testimony by no one. ADR-0010 brake two.
        await SeedAsync(failureStreak: 2);

        using var harness = NewHarness();
        harness.Build(
            CardRedirectThen(ScriptedHandler.Page(Listing())),
            new IncidentThrottle(TimeSpan.Zero));
        await VisitAsync(harness);

        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CardId);
        Assert.Null(card.GoneAt);
        Assert.NotNull(card.QuarantinedUntil);
    }

    [SkippableFact]
    public async Task A_mass_disappearance_trips_the_breaker_instead_of_retiring()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // Ten machine retirements in a day is not ten dupe cleanups — it is a
        // site event, and a human should see it before the eleventh card
        // goes. The suspect benches as before; ONE Critical announces the
        // pattern. ADR-0002's mass-retire objection, answered.
        var now = DateTimeOffset.UtcNow;
        await SeedAsync(failureStreak: 2, more: db =>
        {
            for (var i = 0; i < 10; i++)
            {
                db.Cards.Add(new Card
                {
                    Id = 9000 + i,
                    SetId = SetId,
                    Url = $"/game/pokemon-base-set/gone-{i}",
                    Name = $"Gone #{i}",
                    FirstSeenAt = now.AddDays(-30),
                    LastSeenAt = now.AddDays(-8),
                    GoneAt = now.AddHours(-i),
                });
            }
        });

        using var harness = NewHarness();
        harness.Build(
            CardRedirectThen(ScriptedHandler.Page(Listing((999, "/game/pokemon-base-set/other-1", "Other #1")))),
            new IncidentThrottle(TimeSpan.Zero));
        await VisitAsync(harness);

        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CardId);
        Assert.Null(card.GoneAt);
        Assert.NotNull(card.QuarantinedUntil);
        Assert.Equal(1, harness.Alerter.Raised.Count(a => a.Subject.Contains("disappear")));
    }

    [SkippableFact]
    public async Task A_first_strike_does_not_spend_a_walk()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // Below the bench threshold a 302 is still just bad luck — the
        // listing check costs listing-page fetches and must wait for the
        // pattern the bench itself waits for.
        await SeedAsync(failureStreak: 0);

        using var harness = NewHarness();
        var handler = CardRedirectThen(ScriptedHandler.Page(Listing((999, "/game/x/y", "Y"))));
        harness.Build(handler, new IncidentThrottle(TimeSpan.Zero));
        await VisitAsync(harness);

        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CardId);
        Assert.Equal(1, card.FailureStreak);
        Assert.Null(card.GoneAt);
        Assert.Equal(1, handler.Calls); // the card fetch, and nothing else
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
