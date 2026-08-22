using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Domain.Tests.Fixtures;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.TestSupport;

namespace PokemonInvestBatch.Worker.Tests;

/// <summary>
/// Which alert a capped bucket may raise. Every cap event of Aug 2026 mailed
/// the same Critical — "sales lost" — and on triage four of the first eight
/// were bulk dumps or site-side rewrites where nothing was lost. The cap
/// verdict itself is SalesObservation's; the class tells are pinned to real
/// pages in CapClassificationTests; what belongs to the visitor, and is
/// pinned here, is which subject each class is allowed to wake someone with.
/// </summary>
public class CapAlertTests : DatabaseTest, IDisposable
{
    private const long CardId = 630417;

    private const long SetId = 1;

    /// <summary>The bucket both fixtures rewrite — see BurstFixtureTests.</summary>
    private const string BurstTier = "Grade 9";

    private readonly string _fingerprintDirectory =
        Path.Combine(Path.GetTempPath(), $"fingerprints-{Guid.NewGuid():N}");

    /// <summary>A card holding thirty May rows in the burst tier, none of
    /// which survive on either fixture page — the cap is a given; the class
    /// is what differs between the two fixtures.</summary>
    private async Task SeedCappingCardAsync()
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
            Url = "/game/pokemon-base-set/charizard-4",
            Name = "Charizard #4",
            FirstSeenAt = now,
            LastSeenAt = now,
            LastVisitedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        });
        db.Sales.AddRange(Enumerable.Range(0, 30).Select(i => new Sale
        {
            CardId = CardId,
            Source = "ebay",
            SourceId = $"before-the-burst-{i}",
            SoldOn = new DateOnly(2026, 5, 1).AddDays(i % 30),
            GradeTier = BurstTier,
            PriceCents = 10_000,
            Title = "Charizard sold before the burst",
            CapturedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        }));
        await db.SaveChangesAsync();
    }

    private async Task<RecordingAlerter> VisitAsync(string fixture)
    {
        using var harness = new LaneHarness(ContextOptions(), _fingerprintDirectory);
        harness.Build(new ScriptedHandler(ScriptedHandler.Page(Fixture.Load(fixture))));
        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CardId);
        await harness.Visitor.VisitAsync(db, card, visit: null, "card pages", CancellationToken.None);
        return harness.Alerter;
    }

    [SkippableFact]
    public async Task An_organic_burst_still_alerts_that_sales_were_lost()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await SeedCappingCardAsync();

        var alerter = await VisitAsync("charizard-burst");

        Assert.Contains(alerter.Raised, a => a.Subject == "Sales lost to a hot card");
    }

    [SkippableFact]
    public async Task A_bulk_liquidation_alerts_a_page_roll_and_never_claims_losses()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await SeedCappingCardAsync();

        // Same page, but the thirty burst rows carry one seller's sequential
        // id block — the Gengar/Slowbro signature.
        var alerter = await VisitAsync("charizard-dump");

        Assert.Contains(alerter.Raised, a => a.Subject == "Sale page rolled — no loss expected");
        Assert.DoesNotContain(alerter.Raised, a => a.Subject.Contains("Sales lost"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_fingerprintDirectory))
        {
            Directory.Delete(_fingerprintDirectory, recursive: true);
        }
    }
}
