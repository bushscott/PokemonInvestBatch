using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PokemonInvestBatch.Application.Alerting;
using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Application.Scheduling;
using PokemonInvestBatch.Application.Telemetry;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Worker.Lanes;

/// <summary>
/// The main lane. A "visit" is the whole errand for one card — fetch its
/// detail page (one HTTP request) through the shared polite gate, parse it,
/// write everything it contains in one transaction, mark the card checked.
/// Nothing is written from a page that failed any check. The errand itself
/// lives in CardVisitor (an express visit runs the same one); what this lane
/// owns is the loop around it — the pick, the gate, the pause, and blaming
/// the right party when a visit dies. See GLOSSARY.md for the visit/request
/// vocabulary.
/// </summary>
public sealed class DetailCrawlLane(
    IDbContextFactory<PokemonDbContext> dbFactory,
    CardVisitor visitor,
    PoliteGate gate,
    AdaptiveDelay delay,
    IncidentThrottle throttle,
    IAlerter alerter,
    TimeProvider time,
    IOptions<ScraperOptions> options,
    IOptions<VisitPriorityOptions> priority,
    CrawlMetrics metrics,
    ILogger<DetailCrawlLane> logger) : BackgroundService
{
    // Bound from configuration in Program.cs — every scheduling knob is
    // turnable without a rebuild, not just the two an earlier hand-written
    // initializer happened to copy.
    private readonly VisitPriorityOptions priorityOptions = priority.Value;

    private readonly SameCardFailureBreaker breaker = new();

    private readonly BenchRecheck benchRecheck =
        new(TimeSpan.FromMinutes(options.Value.BenchRecheckIntervalMinutes));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CrawlOneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Detail crawl iteration failed unexpectedly");
                await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
            }
        }
    }

    /// <summary>One errand, start to finish. Internal so the tests can run a
    /// single visit without the forever-loop around it.</summary>
    internal async Task CrawlOneAsync(CancellationToken ct)
    {
        if (delay.ShouldPause)
        {
            if (throttle.ShouldAlert("detail-lane-paused", time.GetUtcNow()))
            {
                await alerter.RaiseAsync(
                    "Detail crawl paused",
                    $"Three consecutive failures against pricecharting.com; sleeping {options.Value.PauseCooldownMinutes}m before probing again.",
                    ct);
            }

            await Task.Delay(TimeSpan.FromMinutes(options.Value.PauseCooldownMinutes), time, ct);
            // Fall through and attempt one probe; a success clears the pause.
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var card = await PickNextCardAsync(db, ct);
        if (card is null)
        {
            logger.LogInformation("No cards to visit yet — waiting for enumeration");
            await Task.Delay(TimeSpan.FromMinutes(5), time, ct);
            return;
        }

        // A still-benched card can only reach us through the bench recheck;
        // remembered so its failures skip the breaker below.
        var isBenchRetry = card.QuarantinedUntil is { } benchedUntil
            && benchedUntil > time.GetUtcNow();

        // The polite wait happens outside the span: card.visit measures work
        // (fetch through commit), not the voluntary sleep before it — same
        // boundary as the visit-duration histogram.
        await gate.WaitTurnAsync(ct);

        using var visit = CrawlTracing.Source.StartActivity("card.visit");
        visit?.SetTag("card.id", card.Id);
        visit?.SetTag("card.name", card.Name);
        // The page's path rides the span so a slow visit can be traced
        // straight to the card page that caused it.
        visit?.SetTag("url.path", card.Url);

        // Every log line written during the visit — EF's transaction chatter
        // included — carries the card, so no mid-visit error ever needs
        // trace archaeology to attribute.
        using var scope = logger.BeginScope("Visiting {CardUrl}", card.Url);

        try
        {
            await visitor.VisitAsync(db, card, visit, "card pages", ct);
            breaker.Reset();
            if (isBenchRetry)
            {
                // A cleared bench is the proof of healing; anything else —
                // parse failure, HTTP trouble — means stand down. The visit
                // itself already recorded why.
                if (card.QuarantinedUntil is null)
                {
                    benchRecheck.RecordSuccess();
                }
                else
                {
                    benchRecheck.RecordFailure(time.GetUtcNow());
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown caught the visit mid-flight: the transaction rolls
            // back whole and LastVisitedAt never advanced. Said out loud so
            // EF's exception-less "error using a transaction" has a witness.
            logger.LogInformation(
                "Visit of {CardUrl} interrupted by shutdown — the card returns to the rotation",
                card.Url);
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Unexpected failures ride the trace, not just the log stream.
            visit?.AddException(e);
            visit?.SetStatus(ActivityStatusCode.Error, e.Message);
            if (isBenchRetry)
            {
                // The breaker exists to attribute repeat failures to a card;
                // a benched card is already attributed. One failed retry
                // re-benches it immediately with the doubled sentence —
                // sending it behind the other benched cards — and stands
                // the recheck down for the interval.
                benchRecheck.RecordFailure(time.GetUtcNow());
                await StrikeUnattributedAsync(card.Id, ct);
            }
            else if (breaker.RecordUnexpectedFailure(card.Id))
            {
                await StrikeUnattributedAsync(card.Id, ct);
            }

            // Rethrown so ExecuteAsync still logs the full exception — the
            // strike above is attribution, never suppression.
            throw;
        }
    }

    /// <summary>
    /// The visit died before any of its own bookkeeping could run, so the
    /// strike is written through a fresh context — the one that failed may
    /// hold a poisoned change tracker or a broken connection.
    /// </summary>
    private async Task StrikeUnattributedAsync(long cardId, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var card = await db.Cards.FirstAsync(c => c.Id == cardId, ct);
            await visitor.RecordStrikeAsync(card, "unexpected", time.GetUtcNow(), ct);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e,
                "Could not record unexpected-failure strike for card {CardId}", cardId);
        }
    }

    /// <summary>Runs the queries VisitSelection's ranking needs, then executes
    /// the choice it returns. Only the chosen card is loaded for real — the
    /// visit writes to it; the ~600 candidates cross the wire as three columns.</summary>
    private async Task<Card?> PickNextCardAsync(PokemonDbContext db, CancellationToken ct)
    {
        var now = time.GetUtcNow();

        long? benchRetryId = null;
        if (benchRecheck.IsSlotOpen(now))
        {
            var benched = await VisitCandidatePool.Benched(db, now).ToListAsync(ct);
            benchRetryId = benchRecheck.TrySelect(benched, now);
        }

        IReadOnlyList<VisitCandidate> candidates = [];
        if (benchRetryId is null)
        {
            candidates = await VisitCandidatePool.LoadAsync(db, now, priorityOptions, ct);
            if (candidates.Count > 0 && candidates[0].State.LastVisitedAt is { } oldest)
            {
                metrics.SetQueueStaleness(now - oldest);
            }
        }

        var choice = VisitSelection.Choose(benchRetryId, candidates, now, priorityOptions);
        switch (choice.Kind)
        {
            case VisitChoiceKind.RetryBenched:
                var retried = await db.Cards.FirstAsync(c => c.Id == choice.CardId!.Value, ct);
                logger.LogInformation(
                    "Bench recheck: retrying card {CardId} ({Name}) ahead of its {Until:u} comeback",
                    retried.Id, retried.Name, retried.QuarantinedUntil);
                return retried;

            case VisitChoiceKind.PreferUnvisited:
                var unvisited = await VisitCandidatePool.Eligible(db, now)
                    .Where(c => c.LastVisitedAt == null)
                    .OrderBy(c => c.Id)
                    .FirstOrDefaultAsync(ct);
                if (unvisited is not null)
                {
                    return unvisited;
                }

                // The backlog is drained; the runner-up gets the slot after all.
                return choice.CardId is { } fallback
                    ? await db.Cards.FirstAsync(c => c.Id == fallback, ct)
                    : null;

            default:
                return await db.Cards.FirstAsync(c => c.Id == choice.CardId!.Value, ct);
        }
    }

}
