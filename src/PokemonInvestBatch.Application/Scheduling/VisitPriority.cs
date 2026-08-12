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

/// <summary>
/// The one owner of "when is a selling card due?". Bound from the Scraper
/// configuration section in Program.cs, so every knob here is turnable in
/// appsettings.Production.json without a rebuild — the Worker's ScraperOptions
/// deliberately holds no copy of any of these (it did once, synced by hand,
/// and only two of four fields were actually wired).
/// </summary>
public sealed record VisitPriorityOptions
{
    /// <summary>The starvation floor: no card waits longer than this.</summary>
    public int MaxDaysBetweenVisits { get; init; } = 30;

    /// <summary>A selling card must be revisited by this fraction of its
    /// burn window (the days its sales rate takes to fill a bucket and
    /// start rolling rows off). Half leaves margin for throttled days.</summary>
    public double BurnWindowSafetyFraction { get; init; } = 0.5;

    /// <summary>
    /// The tighter margin for cards fast enough to actually roll a bucket.
    ///
    /// The safety fraction is a standing bet on how much a card's rate may rise
    /// between two visits: revisiting at fraction f absorbs an acceleration of
    /// up to 1/f, so 0.5 absorbs a doubling, 0.4 two and a half, 0.3 three and
    /// a third.
    ///
    /// The 0.5 bet was lost on 2026-08-10 by Mega Gardevior EX #32, which read
    /// 7.33/day off a page whose two most recent days were its slowest, then ran
    /// at ~15/day — 2.05x, a hair past what 0.5 covers — and rolled its PSA 10
    /// bucket about an hour before the scheduled revisit. Nothing on that page
    /// predicted the acceleration, so the answer is margin rather than a better
    /// estimator.
    ///
    /// Tightened again to 0.3 on 2026-08-11, and deliberately NOT because of
    /// that day's loss: Pikachu #1 was a deflated estimate (see
    /// <see cref="SalesObservation"/>'s reprice test), and margin is the wrong
    /// tool for a wrong number. It moved because the corpus-wide reading is that
    /// rates move further than any single page predicts, and 2.5x is not enough
    /// headroom for that. Measured cost: burn-tier demand 4,437 -> 4,952
    /// visits/day against a ~8,400/day polite ceiling, the 30-day floor counted
    /// in both.
    ///
    /// It is spent only on cards above <see cref="HotRateThreshold"/> because a
    /// cold card cannot roll a bucket however long it waits — tightening
    /// everywhere costs five times as much and buys nothing extra.
    /// </summary>
    public double HotBurnWindowSafetyFraction { get; init; } = 0.3;

    /// <summary>Sales/day at which a card earns the tighter margin. At one a
    /// day a 30-row bucket takes a month to roll, so this is comfortably below
    /// the rate where loss becomes possible.</summary>
    public double HotRateThreshold { get; init; } = 1.0;

    /// <summary>The revisit margin this card has earned: tighter once it sells
    /// fast enough to lose rows.</summary>
    public double SafetyFractionFor(double salesPerDay) =>
        salesPerDay >= HotRateThreshold ? HotBurnWindowSafetyFraction : BurnWindowSafetyFraction;

    /// <summary>
    /// Days after a visit until this card is due again, given the rate read at
    /// that visit. THE single C# definition of the due rule: VisitPriority.Score
    /// asks it, and so does the closed-loop estimator replay — which used to
    /// re-derive the inequality by hand and would have kept validating an old
    /// rule if Score ever changed shape. The only other spelling allowed to
    /// exist is the EF-translatable mirror in
    /// <c>VisitCandidatePool.DueByBurnWindow</c>, which cannot call a method
    /// over entity data; BurnWindowQueryAgreementTests holds the two together.
    /// </summary>
    public double DueAfterDays(double salesPerDay) =>
        SalesObservation.BucketCap / salesPerDay * SafetyFractionFor(salesPerDay);
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
            if (stalenessDays >= options.DueAfterDays(salesPerDay))
            {
                // Checked before the ask so a burn-due card keeps its burn
                // rank: an ask must never demote the card it points at.
                //
                // Rank inside the tier by rows burned, not days waited. Days
                // alone is not urgency here — it says a card selling 1.57/day
                // twelve days back (19 of its 30 rows gone) is more urgent than
                // one selling 7/day four days back (27 gone, hours from
                // rolling), which is backwards for the only thing this tier
                // protects. Kecleon #88 lost rows on 2026-08-11 sitting behind
                // 172 such cards for seventeen hours. It is also the order
                // VisitCandidatePool.DueByBurnWindow admits candidates in, and
                // a pick order that disagrees with the admission order starves
                // whatever the two rank differently.
                return BurnWindowDueTier + (stalenessDays * salesPerDay);
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
