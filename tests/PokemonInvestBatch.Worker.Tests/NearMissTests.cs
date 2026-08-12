using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Domain.Parsing;
using PokemonInvestBatch.Domain.Tests.Fixtures;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.TestSupport;

namespace PokemonInvestBatch.Worker.Tests;

/// <summary>
/// The near-miss flag, end to end: a page whose graded bucket came back almost
/// entirely new stamps <c>near_miss_at</c> inside the visit transaction, and
/// the flag is rewritten from the page at every visit — so a calm revisit
/// clears it without anyone having to remember to. The scheduler halves the
/// card's next interval while the flag is set (see TieredBurnMarginTests);
/// this file owns the persistence half of that promise.
/// </summary>
public class NearMissTests : DatabaseTest, IDisposable
{
    private const string CardUrl = "/game/pokemon-base-set/charizard-4";

    private const long CardId = 630417;

    /// <summary>The bucket charizard-burst rewrites into a 30-row five-day
    /// burst — see BurstFixtureTests, which pins the fixture's shape.</summary>
    private const string BurstTier = "Grade 9";

    private readonly string _fingerprintDirectory =
        Path.Combine(Path.GetTempPath(), $"fingerprints-{Guid.NewGuid():N}");

    private LaneHarness NewHarness() => new(ContextOptions(), _fingerprintDirectory);

    /// <summary>
    /// Seed so the burst page reads as a NEAR miss, not a cap hit: hold a few
    /// of the very rows the page is about to serve. Overlap arithmetic then
    /// says "page full, held rows still on it, but only this many" — margin
    /// small and positive, which is the graduated warning's whole territory.
    /// </summary>
    private async Task SeedHoldingPageRowsAsync(int rowsAlreadyHeld)
    {
        var burstRows = CardDetailParser.Parse(Fixture.Load("charizard-burst"))
            .Sales.Where(s => s.GradeTier == BurstTier)
            .Take(rowsAlreadyHeld)
            .ToList();
        Assert.Equal(rowsAlreadyHeld, burstRows.Count);

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
        db.Cards.Add(new Card
        {
            Id = CardId,
            SetId = 1,
            Url = CardUrl,
            Name = "Charizard #4",
            FirstSeenAt = now,
            LastSeenAt = now,
            LastVisitedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        });
        db.Sales.AddRange(burstRows.Select(s => new Sale
        {
            CardId = CardId,
            Source = s.Source,
            SourceId = s.SourceId,
            SoldOn = s.SoldOn,
            GradeTier = s.GradeTier,
            PriceCents = s.PriceCents,
            Title = s.Title,
            CapturedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero),
        }));
        await db.SaveChangesAsync();
    }

    private async Task VisitAsync(LaneHarness harness)
    {
        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CardId);
        await harness.Visitor.VisitAsync(db, card, visit: null, "card pages", CancellationToken.None);
    }

    [SkippableFact]
    public async Task A_nearly_rolled_bucket_stamps_the_flag_and_a_calm_revisit_clears_it()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // Five of the page's own thirty rows already held: full bucket,
        // overlap five — inside the default NearMissMargin of eight.
        await SeedHoldingPageRowsAsync(rowsAlreadyHeld: 5);

        using var harness = NewHarness();
        harness.Build(new ScriptedHandler(ScriptedHandler.Page(Fixture.Load("charizard-burst"))));
        await VisitAsync(harness);

        await using (var db = NewContext())
        {
            var card = await db.Cards.SingleAsync(c => c.Id == CardId);
            Assert.NotNull(card.NearMissAt);
            Assert.False(card.AnyBucketAtCap);
        }

        // Same page again: every row is ours now, margin thirty — calm. The
        // flag must clear from the page evidence alone.
        await VisitAsync(harness);

        await using (var after = NewContext())
        {
            Assert.Null((await after.Cards.SingleAsync(c => c.Id == CardId)).NearMissAt);
        }
    }

    [SkippableFact]
    public async Task A_cap_hit_is_a_loss_not_a_near_miss()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // Thirty held rows, none of them on the page: the bucket rolled.
        // Zero overlap is the loss itself — reported as at-cap, and the
        // near-miss flag must stay out of its way.
        await using (var db = NewContext())
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
                Id = CardId,
                SetId = 1,
                Url = CardUrl,
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

        using var harness = NewHarness();
        harness.Build(new ScriptedHandler(ScriptedHandler.Page(Fixture.Load("charizard-burst"))));
        await VisitAsync(harness);

        await using var check = NewContext();
        var card = await check.Cards.SingleAsync(c => c.Id == CardId);
        Assert.True(card.AnyBucketAtCap);
        Assert.Null(card.NearMissAt);
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
