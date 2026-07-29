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
            imagesPending: await db.Cards.LongCountAsync(c => c.ImageHash != null && c.ImageFetchedAt == null, ct),
            setsTotal: await db.Sets.LongCountAsync(ct));

        // Worst-case data age. A never-visited card has no observation at
        // all, so the worst case is unbounded — reported as the 9999
        // sentinel rather than pretending it is measurable.
        if (corpusSize > corpusVisited)
        {
            metrics.SetWorstCaseDays(9999);
        }
        else if (corpusVisited > 0)
        {
            var oldest = await db.Cards.MinAsync(c => c.LastVisitedAt, ct);
            metrics.SetWorstCaseDays((time.GetUtcNow() - oldest!.Value).TotalDays);
        }

        // Leading indicator of missed sales: cards whose burn window (days
        // for their sales rate to fill a ~30-row bucket and start rolling
        // rows off) is shorter than the current revisit cycle. These cards
        // survive only by fast-tracking; the count is the pressure gauge.
        var now = time.GetUtcNow();
        var visitsLastDay = await db.Visits.LongCountAsync(
            v => v.Kind == PageKind.CardDetail
                && v.Outcome == VisitOutcome.Parsed
                && v.FetchedAt >= now.AddDays(-1), ct);
        if (visitsLastDay > 0 && corpusSize > 0)
        {
            var cycleDays = (double)corpusSize / visitsLastDay;
            var atRiskSalesRate = SalesObservation.BucketCap / cycleDays;
            metrics.SetCardsAtRisk(await db.Cards.LongCountAsync(
                c => c.ObservedSalesPerDay > atRiskSalesRate, ct));
        }

        metrics.SetSchedulerStats(
            cardsAtCap: await db.Cards.LongCountAsync(c => c.AnyBucketAtCap, ct),
            quarantinedNow: await db.Cards.LongCountAsync(c => c.QuarantinedUntil != null && c.QuarantinedUntil > now, ct));

        metrics.SetTotalRows(
            prices: await db.PriceMonths.LongCountAsync(ct),
            populations: await db.Populations.LongCountAsync(ct),
            sales: await db.Sales.LongCountAsync(ct));
    }
}
