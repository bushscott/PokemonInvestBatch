using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PokemonInvestBatch.Application.Alerting;
using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Application.Telemetry;
using PokemonInvestBatch.Infrastructure.Http;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Worker.Lanes;

/// <summary>
/// The knock on the tombstone. Delisting is a manual verdict and the site
/// offers no way to learn it was wrong: the catalog goes on listing phantom
/// products whose pages never existed, so being listed proves nothing. Only
/// the page itself can testify, so this lane fetches one retired card a
/// month and stays silent when it is still dead — the expected answer. A 200
/// is the news: it warns and raises one alert per card, and changes nothing
/// else. Only the operator may un-delist.
/// </summary>
public sealed class DelistedProbeLane(
    IDbContextFactory<PokemonDbContext> dbFactory,
    PriceChartingClient client,
    PoliteGate gate,
    AdaptiveDelay delay,
    IncidentThrottle throttle,
    IAlerter alerter,
    TimeProvider time,
    IOptions<ScraperOptions> options,
    CrawlMetrics metrics,
    ILogger<DelistedProbeLane> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProbeOneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Delisted probe failed");
            }

            await Task.Delay(
                TimeSpan.FromHours(options.Value.DelistedProbeIntervalHours), time, stoppingToken);
        }
    }

    private async Task ProbeOneAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var minAge = TimeSpan.FromDays(options.Value.DelistedProbeAgeDays);
        var card = await VisitCandidatePool
            .DueForDelistedProbe(db, time.GetUtcNow(), minAge)
            .FirstOrDefaultAsync(ct);
        if (card is null)
        {
            return;
        }

        using var probe = CrawlTracing.Source.StartActivity("delisted.probe");
        probe?.SetTag("card.id", card.Id);
        using var scope = logger.BeginScope("Probing delisted {CardUrl}", card.Url);

        await gate.WaitTurnAsync(ct);
        var fetched = await client.GetAsync(card.Url, ct);
        fetched.RecordOutcome(metrics, delay, "delisted probe");

        // Stamped whatever the answer, so one unreachable card cannot hog the
        // rotation — and stamped before the alert, so a failure to send an
        // email cannot re-probe the same card every cycle.
        card.DelistedProbedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);

        if (fetched is not FetchedPage)
        {
            logger.LogInformation(
                "Delisted card {CardId} ({Name}) still gone — HTTP {Status}",
                card.Id, card.Name, fetched.StatusCode);
            return;
        }

        logger.LogWarning(
            "Delisted card {CardId} ({Name}) is alive again — {CardUrl} now answers 200",
            card.Id, card.Name, card.Url);
        if (throttle.ShouldAlert($"delisted-alive:{card.Id}", time.GetUtcNow()))
        {
            await alerter.RaiseAsync(
                "Delisted card is alive again",
                $"Card {card.Id} ({card.Name}) was retired by hand, but its page now loads: "
                + $"{options.Value.BaseUrl}{card.Url}\n\n"
                + "Nothing was changed — delisting is a manual verdict and only you may reverse "
                + "it. Clear delisted_at to put the card back in rotation; leave it set to keep "
                + "the card retired and hear about it again next month.",
                ct);
        }
    }
}
