using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Application.Scheduling;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.TestSupport;

namespace PokemonInvestBatch.Integration.Tests;

/// <summary>
/// Closes the stress tests' blind spot: they
/// score the full corpus, so they can never notice the candidate pool
/// itself excluding a card the scorer would have picked.
/// </summary>
public class VisitCandidatePoolTests : DatabaseTest
{
    [SkippableFact]
    public async Task A_hot_card_reaches_the_scorer_despite_being_far_from_the_stalest_window()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        await using var db = NewContext();
        var now = DateTimeOffset.UtcNow;

        // 2,000 cold cards, all far staler than the hot card — enough that a
        // staleness-ordered Take(500) can never reach it.
        db.Sets.Add(new CardSet
        {
            Id = 1,
            Slug = "pokemon-base-set",
            Name = "Pokemon Base Set",
            DiscoveredAt = now,
            LastSeenAt = now,
        });
        for (var i = 1; i <= 2_000; i++)
        {
            db.Cards.Add(new Card
            {
                Id = i,
                SetId = 1,
                Url = $"/game/pokemon-base-set/cold-{i}",
                Name = $"Cold #{i}",
                FirstSeenAt = now,
                LastSeenAt = now,
                LastVisitedAt = now.AddDays(-20),
            });
        }

        // Selling 6/day, visited 3 days ago: 18 sales-worth of staleness has
        // consumed over half the 30-row bucket — burn-window due right now.
        var hot = new Card
        {
            Id = 9_999,
            SetId = 1,
            Url = "/game/pokemon-base-set/hot-9999",
            Name = "Hot #9999",
            FirstSeenAt = now,
            LastSeenAt = now,
            LastVisitedAt = now.AddDays(-3),
            ObservedSalesPerDay = 6,
        };
        db.Cards.Add(hot);
        await db.SaveChangesAsync();

        var priorityOptions = new VisitPriorityOptions();
        var pool = await VisitCandidatePool.LoadAsync(db, now, priorityOptions, CancellationToken.None);

        Assert.Contains(pool, c => c.Id == hot.Id);

        var winner = pool.MaxBy(c => VisitPriority.Score(c.State, now, priorityOptions));
        Assert.Equal(hot.Id, winner!.Id);
    }

    [SkippableFact]
    public async Task A_requested_card_reaches_the_scorer_despite_being_fresh()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        await using var db = NewContext();
        var now = DateTimeOffset.UtcNow;

        // 2,000 cards all far staler than the requested one — a merely-hours-
        // stale card can never surface through a staleness-ordered Take(500),
        // so only the requested tier's own window can carry the ask in.
        db.Sets.Add(new CardSet
        {
            Id = 1,
            Slug = "pokemon-base-set",
            Name = "Pokemon Base Set",
            DiscoveredAt = now,
            LastSeenAt = now,
        });
        for (var i = 1; i <= 2_000; i++)
        {
            db.Cards.Add(new Card
            {
                Id = i,
                SetId = 1,
                Url = $"/game/pokemon-base-set/cold-{i}",
                Name = $"Cold #{i}",
                FirstSeenAt = now,
                LastSeenAt = now,
                LastVisitedAt = now.AddDays(-20),
            });
        }

        var requested = new Card
        {
            Id = 9_999,
            SetId = 1,
            Url = "/game/pokemon-base-set/requested-9999",
            Name = "Requested #9999",
            FirstSeenAt = now,
            LastSeenAt = now,
            LastVisitedAt = now.AddDays(-1),
            RefreshRequestedAt = now,
        };
        db.Cards.Add(requested);
        await db.SaveChangesAsync();

        var priorityOptions = new VisitPriorityOptions();
        var pool = await VisitCandidatePool.LoadAsync(db, now, priorityOptions, CancellationToken.None);

        Assert.Contains(pool, c => c.Id == requested.Id);

        var winner = pool.MaxBy(c => VisitPriority.Score(c.State, now, priorityOptions));
        Assert.Equal(requested.Id, winner!.Id);
    }

    [SkippableFact]
    public async Task A_delisted_card_with_a_pending_request_stays_invisible()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

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

        // An ask cannot resurrect a card the operator retired: the request
        // sits inert unless the card is un-delisted by hand.
        db.Cards.Add(new Card
        {
            Id = 1,
            SetId = 1,
            Url = "/game/pokemon-base-set/delisted-1",
            Name = "Delisted #1",
            FirstSeenAt = now,
            LastSeenAt = now,
            LastVisitedAt = now.AddDays(-40),
            RefreshRequestedAt = now,
            DelistedAt = now,
        });
        db.Cards.Add(new Card
        {
            Id = 2,
            SetId = 1,
            Url = "/game/pokemon-base-set/alive-2",
            Name = "Alive #2",
            FirstSeenAt = now,
            LastSeenAt = now,
            LastVisitedAt = now.AddDays(-1),
        });
        await db.SaveChangesAsync();

        var pool = await VisitCandidatePool.LoadAsync(db, now, new VisitPriorityOptions(), CancellationToken.None);
        Assert.DoesNotContain(pool, c => c.Id == 1);
        Assert.Contains(pool, c => c.Id == 2);
    }

    [SkippableFact]
    public async Task A_delisted_card_is_invisible_to_the_pool_and_the_bench()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

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

        // Would top every tier it appears in: stalest of the corpus, burn-window
        // due, benched with the soonest comeback. Delisting must beat them all.
        db.Cards.Add(new Card
        {
            Id = 1,
            SetId = 1,
            Url = "/game/pokemon-base-set/delisted-1",
            Name = "Delisted #1",
            FirstSeenAt = now,
            LastSeenAt = now,
            LastVisitedAt = now.AddDays(-40),
            ObservedSalesPerDay = 6,
            FailureStreak = 3,
            QuarantinedUntil = now.AddDays(1),
            DelistedAt = now,
        });
        db.Cards.Add(new Card
        {
            Id = 2,
            SetId = 1,
            Url = "/game/pokemon-base-set/alive-2",
            Name = "Alive #2",
            FirstSeenAt = now,
            LastSeenAt = now,
            LastVisitedAt = now.AddDays(-1),
        });
        await db.SaveChangesAsync();

        var pool = await VisitCandidatePool.LoadAsync(db, now, new VisitPriorityOptions(), CancellationToken.None);
        Assert.DoesNotContain(pool, c => c.Id == 1);
        Assert.Contains(pool, c => c.Id == 2);

        Assert.Empty(await VisitCandidatePool.Benched(db, now).ToListAsync());

        Assert.Empty(await VisitCandidatePool.PastBurnFraction(db.Cards, now, 0.75).ToListAsync());
    }
}
