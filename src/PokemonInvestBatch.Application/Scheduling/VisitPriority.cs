namespace PokemonInvestBatch.Application.Scheduling;

/// <summary>Scheduler view of a card — the three columns scoring reads.</summary>
public sealed record CardVisitState
{
    public DateTimeOffset? LastVisitedAt { get; init; }

    public double? ObservedSalesPerDay { get; init; }

    public bool AnyBucketAtCap { get; init; }
}

public sealed record VisitPriorityOptions
{
    /// <summary>The starvation floor: no card waits longer than this.</summary>
    public int MaxDaysBetweenVisits { get; init; } = 30;
}

/// <summary>
/// Pure priority scoring for picking the next card to visit. There is no
/// queue: nothing is lined up anywhere — each pick re-scores candidates
/// fresh from Postgres and takes the single highest. Tiers, highest first:
/// never visited → bucket-at-cap (proof of missed sales; dashboard: "cards
/// selling faster than we can track") → starved past the floor → everyone
/// else by staleness (days since last visit) × churn (observed sales/day).
/// </summary>
public static class VisitPriority
{
    private const double UnvisitedTier = 3_000_000;
    private const double CapHitTier = 2_000_000;
    private const double StarvedTier = 1_000_000;

    public static double Score(CardVisitState state, DateTimeOffset now, VisitPriorityOptions options)
    {
        if (state.LastVisitedAt is not { } lastVisited)
        {
            return UnvisitedTier;
        }

        var stalenessDays = (now - lastVisited).TotalDays;
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
