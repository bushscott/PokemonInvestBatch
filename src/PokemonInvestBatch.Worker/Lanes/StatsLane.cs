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
    /// <summary>The at-risk line: the scheduler fast-tracks a selling card at
    /// half its burn window and rows roll off at the full window, so a card
    /// past three quarters means scheduling is falling behind.</summary>
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

        var corpusSize = await db.Cards.LongCountAsync(ct);
        var corpusVisited = await db.Cards.LongCountAsync(c => c.LastVisitedAt != null, ct);
        metrics.SetCorpusStats(
            corpusSize,
            corpusVisited,
            imagesPending: await db.Cards.LongCountAsync(
                c => c.DelistedAt == null && c.ImageHash != null && c.ImageFetchedAt == null, ct),
            setsTotal: await db.Sets.LongCountAsync(ct));

        // Longest wait for a visit: the single most-neglected card. A
        // never-visited card has been waiting since the day enumeration
        // discovered it — measurable, not unbounded. The scheduler's floor
        // (MaxDaysBetweenVisits) promises this never passes 30; the
        // dashboard reds when the promise breaks. Delisted cards are out of
        // the running — never visiting them again is the plan, not neglect.
        var now = time.GetUtcNow();
        var living = db.Cards.Where(c => c.DelistedAt == null);
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
        // fast-tracks every selling card at half its window, so while it
        // keeps up nothing ages this far — any count means scheduling is
        // falling behind, caught with a quarter of the window still left
        // before rows actually roll off unseen.
        metrics.SetCardsAtRisk(
            await VisitCandidatePool.PastBurnFraction(db.Cards, now, AtRiskBurnFraction)
                .Select(c => c.Name + " " + c.Url)
                .ToListAsync(ct));

        metrics.SetSchedulerStats(
            cardsAtCap: await db.Cards.LongCountAsync(c => c.AnyBucketAtCap, ct),
            quarantinedNow: await db.Cards.LongCountAsync(
                c => c.DelistedAt == null && c.QuarantinedUntil != null && c.QuarantinedUntil > now, ct),
            delisted: await db.Cards.LongCountAsync(c => c.DelistedAt != null, ct));

        metrics.SetTotalRows(
            prices: await db.PriceMonths.LongCountAsync(ct),
            populations: await db.Populations.LongCountAsync(ct),
            sales: await db.Sales.LongCountAsync(ct));
    }
}
