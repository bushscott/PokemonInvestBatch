using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Application.Telemetry;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.TestSupport;
using PokemonInvestBatch.Worker.Intake;

namespace PokemonInvestBatch.Worker.Tests;

/// <summary>
/// Filing the queued ask: the intake's whole job is one honest stamp — set
/// once, keep the original place in line, and refuse cards the scheduler
/// would never serve.
/// </summary>
public class RefreshRequestIntakeTests : DatabaseTest, IDisposable
{
    private const long CardId = 630417;

    private CrawlMetrics? _metrics;

    private RefreshRequestIntake NewIntake()
    {
        _metrics = new CrawlMetrics(new AdaptiveDelay(new AdaptiveDelayOptions()));
        return new RefreshRequestIntake(
            new Factory(ContextOptions()),
            TimeProvider.System,
            _metrics,
            NullLogger<RefreshRequestIntake>.Instance);
    }

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
            Url = "/game/pokemon-base-set/charizard-4",
            Name = "Charizard #4",
            FirstSeenAt = now,
            LastSeenAt = now,
        };
        adjust?.Invoke(card);
        db.Cards.Add(card);
        await db.SaveChangesAsync();
    }

    [SkippableFact]
    public async Task Filing_a_refresh_request_stamps_the_card()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await SeedCardAsync();

        var receipt = await NewIntake().FileAsync(CardId, CancellationToken.None);

        Assert.Equal(RefreshRequestOutcome.Accepted, receipt.Outcome);
        Assert.NotNull(receipt.RequestedAt);
        Assert.Null(receipt.QuarantinedUntil);

        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CardId);
        Assert.Equal(receipt.RequestedAt, card.RefreshRequestedAt);
    }

    [SkippableFact]
    public async Task Filing_twice_keeps_the_cards_place_in_line()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // The pool serves oldest ask first; re-stamping would send the card
        // to the back of the very line the caller is trying to move it up.
        await SeedCardAsync();
        var intake = NewIntake();

        var first = await intake.FileAsync(CardId, CancellationToken.None);
        var second = await intake.FileAsync(CardId, CancellationToken.None);

        Assert.Equal(RefreshRequestOutcome.AlreadyPending, second.Outcome);
        Assert.Equal(first.RequestedAt, second.RequestedAt);

        await using var db = NewContext();
        var card = await db.Cards.SingleAsync(c => c.Id == CardId);
        Assert.Equal(first.RequestedAt, card.RefreshRequestedAt);
    }

    [SkippableFact]
    public async Task Filing_for_an_unknown_card_reports_not_found()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        var receipt = await NewIntake().FileAsync(999_999, CancellationToken.None);

        Assert.Equal(RefreshRequestOutcome.UnknownCard, receipt.Outcome);
    }

    [SkippableFact]
    public async Task Filing_for_a_retired_or_delisted_card_is_refused()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // The scheduler would never serve either, so accepting the ask would
        // be a quiet lie — and the card must stay unstamped.
        await SeedCardAsync(c => c.NotACardAt = DateTimeOffset.UtcNow);
        var intake = NewIntake();

        var retired = await intake.FileAsync(CardId, CancellationToken.None);
        Assert.Equal(RefreshRequestOutcome.NotACard, retired.Outcome);

        await using (var db = NewContext())
        {
            var card = await db.Cards.SingleAsync(c => c.Id == CardId);
            card.NotACardAt = null;
            card.DelistedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var delisted = await intake.FileAsync(CardId, CancellationToken.None);
        Assert.Equal(RefreshRequestOutcome.Delisted, delisted.Outcome);

        await using var check = NewContext();
        Assert.Null((await check.Cards.SingleAsync(c => c.Id == CardId)).RefreshRequestedAt);
    }

    [SkippableFact]
    public async Task Filing_for_a_benched_card_is_accepted_with_its_comeback_date()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // The ask survives the sentence — it is served when the bench lets
        // go — and the receipt says when, so the caller can set expectations.
        var comeback = DateTimeOffset.UtcNow.AddHours(6);
        await SeedCardAsync(c =>
        {
            c.FailureStreak = 3;
            c.QuarantinedUntil = comeback;
        });

        var receipt = await NewIntake().FileAsync(CardId, CancellationToken.None);

        Assert.Equal(RefreshRequestOutcome.Accepted, receipt.Outcome);
        Assert.NotNull(receipt.QuarantinedUntil);
    }

    public void Dispose() => _metrics?.Dispose();

    private sealed class Factory(DbContextOptions<PokemonDbContext> contextOptions)
        : IDbContextFactory<PokemonDbContext>
    {
        public PokemonDbContext CreateDbContext() => new(contextOptions);
    }
}
