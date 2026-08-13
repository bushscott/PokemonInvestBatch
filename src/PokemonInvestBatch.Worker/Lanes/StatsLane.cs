using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PokemonInvestBatch.Application.Telemetry;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Worker.Lanes;

/// <summary>
/// Publishes coverage and absolute row totals from Postgres as gauges.
/// Gauges, not summed counters, so worker restarts can never skew the
/// "number that only goes up". Exact counts are fine at this scale;
/// revisit the interval before the corpus reaches tens of millions of rows.
/// </summary>
public sealed class StatsLane(
    IDbContextFactory<PokemonDbContext> dbFactory,
    CrawlMetrics metrics,
    TimeProvider time,
    IOptions<ScraperOptions> options,
    ILogger<StatsLane> logger) : BackgroundService
{
    /// <summary>The at-risk line: the scheduler fast-tracks a selling card
    /// well inside its burn window (VisitPriorityOptions owns the exact
    /// fractions) and rows roll off at the full window, so a card past three
    /// quarters means scheduling is falling behind.</summary>
    private const double AtRiskBurnFraction = 0.75;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Stats sweep failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(options.Value.StatsIntervalMinutes), time, stoppingToken);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Coverage counts only the living: delisted cards and pages that were
        // never cards will never be visited by design, so counting them as
        // "known" leaves the tile permanently short of complete — a gap that
        // reads as work remaining when the work is done.
        var living = VisitCandidatePool.Living(db);
        var corpusSize = await living.LongCountAsync(ct);
        var corpusVisited = await living.LongCountAsync(c => c.LastVisitedAt != null, ct);
        metrics.SetCorpusStats(
            corpusSize,
            corpusVisited,
            imagesPending: await living.LongCountAsync(
                c => c.ImageHash != null && c.ImageFetchedAt == null, ct),
            setsTotal: await db.Sets.LongCountAsync(ct));

        // Longest wait for a visit: the single most-neglected card. A
        // never-visited card has been waiting since the day enumeration
        // discovered it — measurable, not unbounded. The scheduler's floor
        // (MaxDaysBetweenVisits) promises this never passes 30; the
        // dashboard reds when the promise breaks. Delisted cards are out of
        // the running — never visiting them again is the plan, not neglect.
        var now = time.GetUtcNow();
        var oldestVisit = await living.MinAsync(c => c.LastVisitedAt, ct);
        var oldestUnseen = await living.Where(c => c.LastVisitedAt == null)
            .MinAsync(c => (DateTimeOffset?)c.FirstSeenAt, ct);
        var waitingSince = new[] { oldestVisit, oldestUnseen }.Min();
        if (waitingSince is { } since)
        {
            metrics.SetWorstCaseDays((now - since).TotalDays);
        }

        // Leading indicator of missed sales: selling cards past three
        // quarters of their burn window (the days their sales rate takes to
        // fill a ~30-row bucket and start rolling rows off). The scheduler
        // fast-tracks every selling card well before that — the exact
        // fractions live in VisitPriorityOptions and are not restated here,
        // having gone stale twice in two days when they were — so while it
        // keeps up nothing ages this far — any count means scheduling is
        // falling behind, caught with a quarter of the window still left
        // before rows actually roll off unseen.
        //
        // This line stays at 0.75 deliberately. It is a warning about the
        // scheduler falling behind, not a second revisit trigger, so it must
        // sit after every fraction the scheduler actually acts on.
        metrics.SetCardsAtRisk(
            await VisitCandidatePool.PastBurnFraction(db.Cards, now, AtRiskBurnFraction)
                .Select(c => c.Name + " " + c.Url)
                .ToListAsync(ct));

        metrics.SetSchedulerStats(
            // Retired cards keep their sticky at-cap flag but can never calm
            // down via a revisit — counting them would pin the tile red forever.
            cardsAtCap: await living.LongCountAsync(c => c.AnyBucketAtCap, ct),
            quarantinedNow: await living.LongCountAsync(
                c => c.QuarantinedUntil != null && c.QuarantinedUntil > now, ct),
            delisted: await db.Cards.LongCountAsync(c => c.DelistedAt != null, ct));

        metrics.SetTotalRows(
            prices: await db.PriceMonths.LongCountAsync(ct),
            populations: await db.Populations.LongCountAsync(ct),
            sales: await db.Sales.LongCountAsync(ct));

        // Delisted and retired cards are excluded the same way the pool
        // excludes them: their asks are inert, and an inert ask on the gauge
        // would read as a scheduler falling behind.
        metrics.SetRefreshRequestsPending(await living.LongCountAsync(
            c => c.RefreshRequestedAt != null, ct));
    }
}
