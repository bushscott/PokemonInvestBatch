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

    // Six columns cross the wire and nothing is change-tracked — ~650
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
                NearMiss = c.NearMissAt != null,
            },
        };

    /// <summary>
    /// The ONE spelling of "this card has a page worth visiting" — no
    /// tombstone of any kind. Every corpus-shaped query starts here (or
    /// applies <see cref="IsLiving"/> to a queryable it already holds); the
    /// predicate was once hand-copied across thirteen queries in six files,
    /// which is exactly how a new tombstone column gets missed by one of them
    /// and a tile starts lying. The deliberate exceptions, each of which
    /// wants a specific tombstone: the delisted probe, the cards_delisted
    /// gauge, and the enrichment lane (delisted cards stay enrichable,
    /// ADR-0009).
    /// </summary>
    public static readonly System.Linq.Expressions.Expression<Func<Card, bool>> IsLiving =
        c => c.DelistedAt == null && c.NotACardAt == null && c.GoneAt == null;

    /// <summary>See <see cref="IsLiving"/>.</summary>
    public static IQueryable<Card> Living(PokemonDbContext db) => db.Cards.Where(IsLiving);

    /// <summary>Quarantined cards are invisible until their sentence lapses;
    /// tombstoned cards are invisible for good.</summary>
    public static IQueryable<Card> Eligible(PokemonDbContext db, DateTimeOffset now) =>
        Living(db).Where(c => c.QuarantinedUntil == null || c.QuarantinedUntil < now);

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
        Living(db)
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
    /// overdue first. Must stay the same inequality as VisitPriority.Score,
    /// and the same ORDER: this window is bounded, so a scorer that ranked the
    /// admitted candidates differently would keep re-picking whichever end it
    /// prefers and leave the other end of a backlog to burn.
    ///
    /// The fraction is per-card now (cards fast enough to roll a bucket get a
    /// tighter one), and it depends on a column, so the ternary is spelled out
    /// here as a CASE rather than calling VisitPriority.SafetyFractionFor —
    /// EF cannot translate a method over entity data. That duplication is
    /// exactly what BurnWindowQueryAgreementTests exists to catch — if this
    /// drifts looser than the scorer, hot cards stop reaching the candidate
    /// pool at all and the guarantee fails with Score's own tests still green.
    /// </summary>
    public static IQueryable<Card> DueByBurnWindow(
        IQueryable<Card> eligible, DateTimeOffset now, VisitPriorityOptions options)
    {
        var cap = SalesObservation.BucketCap;
        return eligible
            .Where(c => c.LastVisitedAt != null && c.ObservedSalesPerDay > 0)
            // DueAfterDays spelled for EF: the fraction plan capped by the
            // band's interval ceiling (LEAST), halved while the near-miss
            // flag stands. Cold cards get no ceiling — the double.MaxValue
            // arm keeps LEAST from ever binding them.
            .Where(c => (now - c.LastVisitedAt!.Value).TotalDays
                        >= Math.Min(
                                cap * (c.ObservedSalesPerDay!.Value >= options.HotRateThreshold
                                    ? options.HotBurnWindowSafetyFraction
                                    : options.BurnWindowSafetyFraction) / c.ObservedSalesPerDay!.Value,
                                c.ObservedSalesPerDay!.Value >= options.FastCeilingRate
                                    ? options.FastCeilingDays
                                    : c.ObservedSalesPerDay!.Value >= options.HotRateThreshold
                                        ? options.HotCeilingDays
                                        : double.MaxValue)
                            / (c.NearMissAt != null ? 2.0 : 1.0))
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
            .Where(IsLiving)
            .Where(c => c.LastVisitedAt != null && c.ObservedSalesPerDay > 0)
            .Where(c => (now - c.LastVisitedAt!.Value).TotalDays * c.ObservedSalesPerDay!.Value
                        > threshold);
    }

    /// <summary>Most set siblings one cap event may fast-track: half the
    /// refresh tier's serve window, so hype in one set can never crowd an
    /// outside ask out of the queue for long.</summary>
    public const int SetContagionTake = 25;

    /// <summary>
    /// Hype is set-shaped: when one card's bucket caps, its set's hottest
    /// known sellers are next in the blast radius. This picks them — best
    /// rate first, skipping cards already asked for and cards with no page
    /// worth visiting. Quarantined siblings stay in deliberately: the ask
    /// waits out the bench, exactly like any other refresh request.
    /// </summary>
    public static IQueryable<long> HottestSetSiblings(
        PokemonDbContext db, long setId, long exceptCardId) =>
        Living(db)
            .Where(c => c.SetId == setId && c.Id != exceptCardId)
            .Where(c => c.ObservedSalesPerDay > 0 && c.RefreshRequestedAt == null)
            .OrderByDescending(c => c.ObservedSalesPerDay)
            .Take(SetContagionTake)
            .Select(c => c.Id);
}
