using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PokemonInvestBatch.Application.Alerting;
using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Application.Telemetry;
using PokemonInvestBatch.Infrastructure.Http;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Worker.Lanes;

/// <summary>
/// The knock on the tombstone — two tombstones now, with two very different
/// contracts. A hand-delisted card gets a raw fetch once a month and a 200
/// changes NOTHING but the operator's inbox: the verdict is theirs alone.
/// A machine-retired (gone) card is the machine's own business, so its probe
/// is the FULL visit errand on a self-doubling clock (1d, 2d, 4d… capped at
/// 30): a 200 parses the page, writes the fresh rows, and clears the verdict
/// in the same transaction — a comeback is a log line, not an email. Silence
/// when still dead is the expected answer either way.
/// </summary>
public sealed class DelistedProbeLane(
    IDbContextFactory<PokemonDbContext> dbFactory,
    PriceChartingClient client,
    CardVisitor visitor,
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
                await ProbeDueAsync(stoppingToken);
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

    /// <summary>Everything due, both populations, one fetch per polite slot.
    /// Public on the EnrichmentLane.RunSweepAsync precedent: tests drive one
    /// sweep without the forever-loop around it. Each pick re-queries, so the
    /// stamp written for one card is what advances the loop to the next.</summary>
    public async Task ProbeDueAsync(CancellationToken ct)
    {
        while (await ProbeNextDelistedAsync(ct))
        {
        }

        while (await ProbeNextGoneAsync(ct))
        {
        }
    }

    private async Task<bool> ProbeNextDelistedAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var minAge = TimeSpan.FromDays(options.Value.DelistedProbeAgeDays);
        var card = await VisitCandidatePool
            .DueForDelistedProbe(db, time.GetUtcNow(), minAge)
            .FirstOrDefaultAsync(ct);
        if (card is null)
        {
            return false;
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
            return true;
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

        return true;
    }

    private async Task<bool> ProbeNextGoneAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var card = await VisitCandidatePool
            .DueForGoneProbe(db, time.GetUtcNow())
            .FirstOrDefaultAsync(ct);
        if (card is null)
        {
            return false;
        }

        using var probe = CrawlTracing.Source.StartActivity("gone.probe");
        probe?.SetTag("card.id", card.Id);
        using var scope = logger.BeginScope("Probing gone {CardUrl}", card.Url);

        // The full errand, not a peek: a page that answers is parsed and
        // written, and CardPageWriter clears gone_at in that same commit.
        // The visitor knows a gone card builds no streak and re-litigates
        // no verdict — a still-dead page just falls through to the stamp.
        await gate.WaitTurnAsync(ct);
        var result = await visitor.VisitAsync(db, card, probe, "gone probe", ct);

        if (result.Outcome == VisitOutcome.Parsed)
        {
            logger.LogWarning(
                "Card {CardId} ({Name}) returned from retirement — {CardUrl} answers again "
                + "and its fresh page is already written",
                card.Id, card.Name, card.Url);
            return true;
        }

        card.DelistedProbedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Gone card {CardId} ({Name}) still gone — HTTP {Status}; the silence doubles",
            card.Id, card.Name, result.HttpStatus);
        return true;
    }
}
