namespace PokemonInvestBatch.Application.Scheduling;

/// <summary>Scheduler view of a card — the four columns scoring reads.</summary>
public sealed record CardVisitState
{
    public DateTimeOffset? LastVisitedAt { get; init; }

    public double? ObservedSalesPerDay { get; init; }

    public bool AnyBucketAtCap { get; init; }

    /// <summary>Another app asked for this card via the intake API.</summary>
    public bool RefreshRequested { get; init; }
}

public sealed record VisitPriorityOptions
{
    /// <summary>The starvation floor: no card waits longer than this.</summary>
    public int MaxDaysBetweenVisits { get; init; } = 30;

    /// <summary>A selling card must be revisited by this fraction of its
    /// burn window (the days its sales rate takes to fill a bucket and
    /// start rolling rows off). Half leaves margin for throttled days.</summary>
    public double BurnWindowSafetyFraction { get; init; } = 0.5;
}

/// <summary>
/// Pure priority scoring for picking the next card to visit. There is no
/// queue: nothing is lined up anywhere — each pick re-scores candidates
/// fresh from Postgres and takes the single highest. Tiers, highest first:
/// due by burn window (sales will start rolling off the site's ~30-row
/// bucket if we wait — the zero-missed-sales guarantee) → refresh requested
/// (another app's ask via the intake API) → never visited → bucket-at-cap
/// (proof sales were already missed) → starved past the floor → everyone
/// else by staleness (days since last visit) × churn (observed sales/day).
/// Prevention outranks discovery: an unvisited backlog (first pass, a new
/// set) must never make a known-hot card lose sales — and an ask, however
/// urgent to its caller, must never outrank prevention.
/// </summary>
public static class VisitPriority
{
    private const double BurnWindowDueTier = 3_000_000;
    private const double RefreshRequestedTier = 2_750_000;
    private const double UnvisitedTier = 2_500_000;
    private const double CapHitTier = 2_000_000;
    private const double StarvedTier = 1_000_000;

    public static double Score(CardVisitState state, DateTimeOffset now, VisitPriorityOptions options)
    {
        if (state.LastVisitedAt is not { } lastVisited)
        {
            // A requested-but-never-visited card takes the ask's tier, not the
            // backlog's — the ask is what puts it ahead of the whole backlog.
            return state.RefreshRequested ? RefreshRequestedTier : UnvisitedTier;
        }

        var stalenessDays = (now - lastVisited).TotalDays;

        // Prevention outranks the already-burned: losing new rows forever is
        // worse than re-checking a card whose bucket already rolled.
        if (state.ObservedSalesPerDay is { } salesPerDay && salesPerDay > 0)
        {
            var burnWindowDays = SalesObservation.BucketCap / salesPerDay;
            if (stalenessDays >= burnWindowDays * options.BurnWindowSafetyFraction)
            {
                // Checked before the ask so a burn-due card keeps its burn
                // rank: an ask must never demote the card it points at.
                return BurnWindowDueTier + stalenessDays;
            }
        }

        if (state.RefreshRequested)
        {
            return RefreshRequestedTier + stalenessDays;
        }

        if (state.AnyBucketAtCap)
        {
            return CapHitTier + stalenessDays;
        }

        if (stalenessDays >= options.MaxDaysBetweenVisits)
        {
            return StarvedTier + stalenessDays;
        }

        return stalenessDays * (1 + (state.ObservedSalesPerDay ?? 0));
    }
}
