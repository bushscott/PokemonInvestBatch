using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Application.Telemetry;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Worker.Intake;

public enum RefreshRequestOutcome
{
    Accepted,

    /// <summary>An earlier ask is still standing; its original timestamp keeps
    /// the card's place in the oldest-ask-first window.</summary>
    AlreadyPending,

    UnknownCard,

    NotACard,

    Delisted,

    /// <summary>Machine-retired: the site removed the product. Unlike the two
    /// above this can reverse itself — the probe re-checks on a doubling
    /// clock — so the caller's move is to try again later, or use an express
    /// visit, which serves gone cards precisely because a 200 un-retires.</summary>
    Gone,
}

public sealed record RefreshRequestReceipt(
    RefreshRequestOutcome Outcome,
    DateTimeOffset? RequestedAt,
    DateTimeOffset? QuarantinedUntil);

/// <summary>
/// Files the queued ask: stamp <c>cards.refresh_requested_at</c> and let the
/// detail lane serve it at the requested tier — the next crawl slot, unless a
/// burn-window-due card owns that slot, in which case the slot after.
/// </summary>
public sealed class RefreshRequestIntake(
    IDbContextFactory<PokemonDbContext> dbFactory,
    TimeProvider time,
    CrawlMetrics metrics,
    ILogger<RefreshRequestIntake> logger)
{
    public async Task<RefreshRequestReceipt> FileAsync(long cardId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var card = await db.Cards.FirstOrDefaultAsync(c => c.Id == cardId, ct);
        if (card is null)
        {
            return new RefreshRequestReceipt(RefreshRequestOutcome.UnknownCard, null, null);
        }

        // The scheduler would never serve either of these, so accepting the
        // ask would be a quiet lie to the caller.
        if (card.NotACardAt is not null)
        {
            return new RefreshRequestReceipt(RefreshRequestOutcome.NotACard, null, null);
        }

        if (card.DelistedAt is not null)
        {
            return new RefreshRequestReceipt(RefreshRequestOutcome.Delisted, null, null);
        }

        if (card.GoneAt is not null)
        {
            return new RefreshRequestReceipt(RefreshRequestOutcome.Gone, null, null);
        }

        var now = time.GetUtcNow();

        // A benched card's ask is accepted and survives the sentence; the
        // comeback date rides the receipt so the caller can set expectations.
        var quarantinedUntil = card.QuarantinedUntil is { } until && until > now
            ? until
            : (DateTimeOffset?)null;

        if (card.RefreshRequestedAt is { } alreadyAsked)
        {
            return new RefreshRequestReceipt(RefreshRequestOutcome.AlreadyPending, alreadyAsked, quarantinedUntil);
        }

        card.RefreshRequestedAt = now;
        await db.SaveChangesAsync(ct);
        metrics.RecordRefreshRequested();
        logger.LogInformation("Refresh request filed for card {CardId} ({Name})", card.Id, card.Name);
        return new RefreshRequestReceipt(RefreshRequestOutcome.Accepted, now, quarantinedUntil);
    }
}
