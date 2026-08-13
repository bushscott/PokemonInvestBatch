using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Application.Alerting;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Worker.Lanes;

/// <summary>What the set listing said about a 302ing card.</summary>
public enum MissingCardVerdict
{
    /// <summary>The listing proved nothing — walk incomplete, listing empty,
    /// the card still listed at its dead URL, or the breaker tripped. The
    /// bench keeps custody exactly as before this path existed.</summary>
    NoVerdict,

    /// <summary>The listing carries the product id under a different URL and
    /// the walk already healed the row — a rename, recovered same-day
    /// instead of waiting for the weekly walk.</summary>
    Healed,

    /// <summary>A completed, non-empty walk of the card's own set no longer
    /// carries its product id: the site removed the product.</summary>
    Gone,
}

/// <summary>
/// The diagnosis every manual retirement repeated, mechanized: when a card
/// 302s at the bench threshold, ask its own set's listing — the catalog is
/// the ground truth the redirect target never was (it points at a search
/// page). One walk resolves the whole 302 family: renamed heals, removed
/// retires, phantom benches. ADR-0010 records why this is allowed to exist
/// despite ADR-0002: the verdict is reversible (the probe's next 200 undoes
/// it) and mass retirement is braked here, not hoped against.
/// </summary>
public sealed class MissingCardResolver(
    SetWalker walker,
    IncidentThrottle throttle,
    IAlerter alerter,
    ILogger<MissingCardResolver> logger)
{
    /// <summary>ADR-0010's circuit breaker: machine retirements allowed per
    /// trailing day before the eleventh suspect benches instead and one
    /// Critical announces the pattern. Ten dupe cleanups in a day has never
    /// happened; ten in an hour is a site event a human should see.</summary>
    public const int MaxGoneVerdictsPerDay = 10;

    public async Task<MissingCardVerdict> ResolveAsync(
        PokemonDbContext db, Card card, DateTimeOffset now, CancellationToken ct)
    {
        var failingUrl = card.Url;
        var walk = await walker.WalkSetAsync(card.SetId, ct);

        // The walk healed (or didn't) in its own context; this visit's view
        // of the card is stale until reloaded.
        await db.Entry(card).ReloadAsync(ct);

        if (card.Url != failingUrl)
        {
            logger.LogInformation(
                "Card {CardId} ({Name}) was renamed: the set listing healed {OldUrl} to {NewUrl}",
                card.Id, card.Name, failingUrl, card.Url);
            return MissingCardVerdict.Healed;
        }

        if (!walk.Completed || walk.CardsSeen == 0)
        {
            // Absence of evidence: an abandoned or empty walk proves nothing
            // about any card it did not reach.
            return MissingCardVerdict.NoVerdict;
        }

        if (card.LastSeenAt >= now)
        {
            // The catalog insists the dead URL is right — the phantom class.
            // Only a page can settle that argument, and its page is a 302.
            return MissingCardVerdict.NoVerdict;
        }

        var goneToday = await db.Cards.CountAsync(
            c => c.GoneAt != null && c.GoneAt > now.AddDays(-1), ct);
        if (goneToday >= MaxGoneVerdictsPerDay)
        {
            if (throttle.ShouldAlert("mass-disappearance", now))
            {
                await alerter.RaiseAsync(
                    "Mass disappearance: auto-retirement paused",
                    $"{goneToday} cards were machine-retired in the last day and card {card.Id} "
                    + $"({card.Name}) just qualified as well. That is not dupe cleanup — something "
                    + "site-wide moved (or our listing parser broke in a way the walks cannot see). "
                    + "Further suspects are benched, not retired, until this calms down; nothing "
                    + "needs undoing, because nothing more will be done.",
                    ct);
            }

            return MissingCardVerdict.NoVerdict;
        }

        logger.LogInformation(
            "Card {CardId} ({Name}) is gone: a completed walk of its set ({CardsSeen} products) "
            + "no longer lists product id {CardId} — machine-retired; the probe re-checks tomorrow",
            card.Id, card.Name, walk.CardsSeen, card.Id);
        return MissingCardVerdict.Gone;
    }
}
