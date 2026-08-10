using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Domain.Tests.Fixtures;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.TestSupport;

namespace PokemonInvestBatch.Worker.Tests;

/// <summary>
/// The whole contagion errand, end to end: a page whose graded bucket provably
/// turned over, through the real visitor into a real database, out to the set
/// siblings' refresh asks. The visitor is driven directly — the lane's picker
/// would happily serve the very siblings these tests are asserting about.
/// </summary>
public class SetContagionTests : DatabaseTest, IDisposable
{
    private const string CardUrl = "/game/pokemon-base-set/charizard-4";

    private const long CapCardId = 630417;

    private const long HotSetId = 1;

    private const long OtherSetId = 2;

    private static readonly DateTimeOffset EarlierAsk = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly string _fingerprintDirectory =
        Path.Combine(Path.GetTempPath(), $"fingerprints-{Guid.NewGuid():N}");

    private LaneHarness NewHarness() => new(ContextOptions(), _fingerprintDirectory);

    private Card NewCard(long id, long setId, string name, Action<Card>? adjust = null)
    {
        var now = DateTimeOffset.UtcNow;
        var card = new Card
        {
            Id = id,
            SetId = setId,
            Url = $"/game/set-{setId}/{name}",
            Name = name,
            FirstSeenAt = now,
            LastSeenAt = now,
            LastVisitedAt = now.AddHours(-1),
        };
        adjust?.Invoke(card);
        return card;
    }

    private async Task SeedAsync(IEnumerable<Card> cards)
    {
        await using var db = NewContext();
        var now = DateTimeOffset.UtcNow;
        db.Sets.Add(new CardSet
        {
            Id = HotSetId,
            Slug = "pokemon-base-set",
            Name = "Pokemon Base Set",
            DiscoveredAt = now,
            LastSeenAt = now,
        });
        db.Sets.Add(new CardSet
        {
            Id = OtherSetId,
            Slug = "pokemon-jungle",
            Name = "Pokemon Jungle",
            DiscoveredAt = now,
            LastSeenAt = now,
        });

        // The card about to cap: last seen before the fixture's June burst, so
        // the full bucket's oldest row is provably newer than the last look.
        db.Cards.Add(NewCard(CapCardId, HotSetId, "Charizard #4", c =>
        {
            c.Url = CardUrl;
            c.LastVisitedAt = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        }));
        db.Cards.AddRange(cards);
        await db.SaveChangesAsync();
    }

    private async Task VisitCappingCardAsync(LaneHarness harness)
    {
        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CapCardId);
        await harness.Visitor.VisitAsync(db, card, visit: null, "card pages", CancellationToken.None);
    }

    [SkippableFact]
    public async Task A_capping_card_fast_tracks_its_sets_hottest_sellers()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // Thirty sellers (rates 1..30) prove the top-25 bound; the rest prove
        // the exclusions: no rate, already asked, retired two ways, wrong set.
        var sellers = Enumerable.Range(1, 30)
            .Select(i => NewCard(1000 + i, HotSetId, $"seller-{i}", c => c.ObservedSalesPerDay = i));
        await SeedAsync(sellers.Concat(
        [
            NewCard(2001, HotSetId, "no-sales", c => c.ObservedSalesPerDay = 0),
            NewCard(2002, HotSetId, "already-asked", c =>
            {
                c.ObservedSalesPerDay = 40;
                c.RefreshRequestedAt = EarlierAsk;
            }),
            NewCard(2003, HotSetId, "delisted-seller", c =>
            {
                c.ObservedSalesPerDay = 40;
                c.DelistedAt = DateTimeOffset.UtcNow;
            }),
            NewCard(2004, HotSetId, "retired-not-a-card", c =>
            {
                c.ObservedSalesPerDay = 40;
                c.NotACardAt = DateTimeOffset.UtcNow;
            }),
            NewCard(2005, OtherSetId, "hot-in-another-set", c => c.ObservedSalesPerDay = 40),
        ]));

        using var harness = NewHarness();
        harness.Build(new ScriptedHandler(ScriptedHandler.Page(Fixture.Load("charizard-burst"))));
        await VisitCappingCardAsync(harness);

        await using var db = NewContext();
        Assert.True((await db.Cards.SingleAsync(c => c.Id == CapCardId)).AnyBucketAtCap);

        var stampedSellers = await db.Cards
            .Where(c => c.Id >= 1001 && c.Id <= 1030 && c.RefreshRequestedAt != null)
            .Select(c => c.ObservedSalesPerDay!.Value)
            .ToListAsync();
        Assert.Equal(25, stampedSellers.Count);
        Assert.Equal(6.0, stampedSellers.Min());

        Assert.Equal(EarlierAsk, (await db.Cards.SingleAsync(c => c.Id == 2002)).RefreshRequestedAt);
        var untouched = await db.Cards
            .Where(c => new long[] { 2001, 2003, 2004, 2005 }.Contains(c.Id))
            .ToListAsync();
        Assert.All(untouched, c => Assert.Null(c.RefreshRequestedAt));
    }

    [SkippableFact]
    public async Task A_still_capped_revisit_does_not_restamp()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await SeedAsync([NewCard(1001, HotSetId, "seller-1", c => c.ObservedSalesPerDay = 5)]);

        using var harness = NewHarness();
        harness.Build(new ScriptedHandler(ScriptedHandler.Page(Fixture.Load("charizard-burst"))));
        await VisitCappingCardAsync(harness);

        await using (var db = NewContext())
        {
            var seller = await db.Cards.SingleAsync(c => c.Id == 1001);
            Assert.NotNull(seller.RefreshRequestedAt);

            // The ask is served and cleared; the same card capping again in the
            // same episode must not re-ring the doorbell — only the false→true
            // edge stamps.
            seller.RefreshRequestedAt = null;
            await db.SaveChangesAsync();
        }

        await VisitCappingCardAsync(harness);

        await using var after = NewContext();
        Assert.Null((await after.Cards.SingleAsync(c => c.Id == 1001)).RefreshRequestedAt);
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
