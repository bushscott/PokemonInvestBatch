using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PokemonInvestBatch.Application.Telemetry;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Worker.Lanes;

/// <summary>
/// Refreshes the dashboard gauges — corpus coverage and absolute row totals —
/// from Postgres. Gauges, not summed counters, so worker restarts can never
/// skew the "number that only goes up". Exact counts are fine at this scale;
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

        metrics.SetCorpusStats(
            corpusSize: await db.Cards.LongCountAsync(ct),
            corpusVisited: await db.Cards.LongCountAsync(c => c.LastVisitedAt != null, ct),
            imagesPending: await db.Cards.LongCountAsync(c => c.ImageHash != null && c.ImageFetchedAt == null, ct));

        var now = time.GetUtcNow();
        metrics.SetSchedulerStats(
            cardsAtCap: await db.Cards.LongCountAsync(c => c.AnyBucketAtCap, ct),
            quarantinedNow: await db.Cards.LongCountAsync(c => c.QuarantinedUntil != null && c.QuarantinedUntil > now, ct));

        metrics.SetTotalRows(
            prices: await db.PriceMonths.LongCountAsync(ct),
            populations: await db.Populations.LongCountAsync(ct),
            sales: await db.Sales.LongCountAsync(ct));
    }
}
