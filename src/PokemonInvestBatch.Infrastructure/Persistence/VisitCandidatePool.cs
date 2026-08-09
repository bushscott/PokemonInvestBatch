using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Application.Scheduling;

namespace PokemonInvestBatch.Infrastructure.Persistence;

/// <summary>
/// Bounded candidate queries for the detail lane's next-card pick. Each
/// VisitPriority tier whose members a staleness-ordered window cannot see
/// gets its own query, so the scorer is never handed a pool that excludes
/// the cards its tiers exist for. The burn-window query is the load-bearing
/// one: a hot card is due while barely stale — invisible among the stalest
/// N of a large corpus — and the zero-missed-sales guarantee holds only if
/// it can still reach the scorer.
/// </summary>
public static class VisitCandidatePool
{
    /// <summary>The staleness window — general-population candidates.</summary>
    public const int StalestTake = 500;

    /// <summary>Bound per targeted tier query.</summary>
    public const int TierTake = 50;

    public static async Task<IReadOnlyList<VisitCandidate>> LoadAsync(
        PokemonDbContext db, DateTimeOffset now, VisitPriorityOptions options, CancellationToken ct)
    {
        var eligible = Eligible(db, now);
        var stalest = await eligible
            .OrderBy(c => c.LastVisitedAt)
            .Take(StalestTake)
            .Select(ToCandidate)
            .ToListAsync(ct);
        var capHits = await eligible
            .Where(c => c.AnyBucketAtCap)
            .OrderBy(c => c.LastVisitedAt)
            .Take(TierTake)
            .Select(ToCandidate)
            .ToListAsync(ct);
        var dueByBurn = await DueByBurnWindow(eligible, now, options)
            .Select(ToCandidate)
            .ToListAsync(ct);
        var requested = await RefreshRequested(eligible)
            .Select(ToCandidate)
            .ToListAsync(ct);

        // Stalest-first order is part of the contract: callers read the
        // queue-staleness gauge off the first element.
        return stalest.Concat(capHits).Concat(dueByBurn).Concat(requested)
            .DistinctBy(c => c.Id).ToList();
    }

    // Five columns cross the wire and nothing is change-tracked — ~650
    // candidates are read per pick and exactly one card is ever written.
    private static readonly System.Linq.Expressions.Expression<Func<Card, VisitCandidate>> ToCandidate =
        c => new VisitCandidate
        {
            Id = c.Id,
            State = new CardVisitState
            {
                LastVisitedAt = c.LastVisitedAt,
                ObservedSalesPerDay = c.ObservedSalesPerDay,
                AnyBucketAtCap = c.AnyBucketAtCap,
                RefreshRequested = c.RefreshRequestedAt != null,
            },
        };

    /// <summary>Quarantined cards are invisible until their sentence lapses;
    /// delisted cards and things that were never cards are invisible for
    /// good.</summary>
    public static IQueryable<Card> Eligible(PokemonDbContext db, DateTimeOffset now) =>
        db.Cards.Where(c =>
            c.DelistedAt == null && c.NotACardAt == null
            && (c.QuarantinedUntil == null || c.QuarantinedUntil < now));

    /// <summary>
    /// The retry queue, for the bench recheck: still-benched cards,
    /// soonest comeback first — a failed retry's doubled sentence pushes it
    /// behind the others, so the recheck rotates instead of fixating.
    /// Delisted cards are excluded: the retry exists to catch a page coming
    /// back, and a product pulled from the site has no page to come back.
    /// Pages that were never cards are excluded for a blunter reason — there is
    /// nothing to come back to, so retrying one forever is pure waste.
    /// Bounded like the other tier windows; two narrow columns cross the wire.
    /// </summary>
    public static IQueryable<BenchedCandidate> Benched(PokemonDbContext db, DateTimeOffset now) =>
        db.Cards
            .Where(c => c.DelistedAt == null && c.NotACardAt == null)
            .Where(c => c.QuarantinedUntil != null && c.QuarantinedUntil >= now)
            .OrderBy(c => c.QuarantinedUntil)
            .Take(TierTake)
            .Select(c => new BenchedCandidate
            {
                Id = c.Id,
                QuarantinedUntil = c.QuarantinedUntil!.Value,
            });

    /// <summary>
    /// The one query that deliberately looks at delisted cards: the rare
    /// "are you back?" probe. A retired card's page is the only witness that
    /// can answer — the catalog keeps listing phantom products whose pages
    /// never existed — so the probe asks the page itself, oldest-asked
    /// first (never-asked leads), one card at a time.
    /// </summary>
    public static IQueryable<Card> DueForDelistedProbe(
        PokemonDbContext db, DateTimeOffset now, TimeSpan minAge)
    {
        var cutoff = now - minAge;
        return db.Cards
            .Where(c => c.DelistedAt != null)
            .Where(c => c.DelistedProbedAt == null || c.DelistedProbedAt < cutoff)
            .OrderBy(c => c.DelistedProbedAt != null)
            .ThenBy(c => c.DelistedProbedAt)
            .Take(1);
    }

    /// <summary>
    /// Cards another app asked to refresh, oldest ask first — the intake
    /// tier's own window, since a merely-hours-stale card is invisible to
    /// every staleness-ordered query.
    /// </summary>
    public static IQueryable<Card> RefreshRequested(IQueryable<Card> eligible) =>
        eligible
            .Where(c => c.RefreshRequestedAt != null)
            .OrderBy(c => c.RefreshRequestedAt)
            .Take(TierTake);

    /// <summary>
    /// VisitPriority's burn-window condition — staleness × sales rate has
    /// consumed the safety fraction of the bucket — translated to SQL, most
    /// overdue first. Must stay the same inequality as VisitPriority.Score.
    /// </summary>
    public static IQueryable<Card> DueByBurnWindow(
        IQueryable<Card> eligible, DateTimeOffset now, VisitPriorityOptions options)
    {
        var dueThreshold = SalesObservation.BucketCap * options.BurnWindowSafetyFraction;
        return eligible
            .Where(c => c.LastVisitedAt != null && c.ObservedSalesPerDay > 0)
            .Where(c => (now - c.LastVisitedAt!.Value).TotalDays * c.ObservedSalesPerDay!.Value
                        >= dueThreshold)
            .OrderByDescending(c => (now - c.LastVisitedAt!.Value).TotalDays
                                    * c.ObservedSalesPerDay!.Value)
            .Take(TierTake);
    }

    /// <summary>
    /// Selling cards whose staleness × sales rate has consumed the given
    /// fraction of the bucket — the stats sweep's at-risk count. Unbounded
    /// and quarantine-blind on purpose: a quarantined hot card is still
    /// losing margin, so it must still be counted. Delisted cards and pages
    /// that were never cards are the two exclusions — with no page worth
    /// visiting again their staleness only grows, and a permanent false
    /// alarm teaches everyone to ignore it.
    /// </summary>
    public static IQueryable<Card> PastBurnFraction(
        IQueryable<Card> cards, DateTimeOffset now, double fraction)
    {
        var threshold = SalesObservation.BucketCap * fraction;
        return cards
            .Where(c => c.DelistedAt == null && c.NotACardAt == null)
            .Where(c => c.LastVisitedAt != null && c.ObservedSalesPerDay > 0)
            .Where(c => (now - c.LastVisitedAt!.Value).TotalDays * c.ObservedSalesPerDay!.Value
                        > threshold);
    }
}
