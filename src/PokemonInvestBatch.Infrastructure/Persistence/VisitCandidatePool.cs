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

        // Stalest-first order is part of the contract: callers read the
        // queue-staleness gauge off the first element.
        return stalest.Concat(capHits).Concat(dueByBurn).DistinctBy(c => c.Id).ToList();
    }

    // Four columns cross the wire and nothing is change-tracked — ~600
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
            },
        };

    /// <summary>Quarantined cards are invisible until their sentence lapses.</summary>
    public static IQueryable<Card> Eligible(PokemonDbContext db, DateTimeOffset now) =>
        db.Cards.Where(c => c.QuarantinedUntil == null || c.QuarantinedUntil < now);

    /// <summary>
    /// VisitPriority's burn-window condition — staleness × sales rate has
    /// consumed the safety fraction of the bucket — translated to SQL, most
    /// overdue first. Must stay the same inequality as VisitPriority.Score.
    /// </summary>
    /// <summary>One scoring candidate: the card's id plus the three facts the
    /// scorer reads. Nothing else leaves the database until a winner is picked.</summary>
    public sealed record VisitCandidate
    {
        public required long Id { get; init; }

        public required CardVisitState State { get; init; }
    }

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
    /// and unfiltered on purpose: a quarantined hot card is still losing
    /// margin, so it must still be counted.
    /// </summary>
    public static IQueryable<Card> PastBurnFraction(
        IQueryable<Card> cards, DateTimeOffset now, double fraction)
    {
        var threshold = SalesObservation.BucketCap * fraction;
        return cards
            .Where(c => c.LastVisitedAt != null && c.ObservedSalesPerDay > 0)
            .Where(c => (now - c.LastVisitedAt!.Value).TotalDays * c.ObservedSalesPerDay!.Value
                        > threshold);
    }
}
