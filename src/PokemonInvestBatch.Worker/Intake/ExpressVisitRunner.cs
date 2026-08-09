using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Application.Telemetry;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.Worker.Lanes;

namespace PokemonInvestBatch.Worker.Intake;

/// <summary>What an express visit came to, for the endpoint to translate.</summary>
public abstract record ExpressResult;

public sealed record ExpressUnknownCard : ExpressResult;

public sealed record ExpressNotACard : ExpressResult;

public sealed record ExpressTimedOut : ExpressResult;

public sealed record ExpressErrored(string Reason) : ExpressResult;

public sealed record ExpressCompleted(CardVisitor.VisitResult Visit, TimeSpan Duration, bool Coalesced) : ExpressResult;

/// <summary>
/// The instantaneous path: update this card now, separately from the schedule,
/// while the caller waits. It runs the same errand as the lane (CardVisitor),
/// with the same failure attribution — but skips the pick and the polite gate
/// by explicit decision. Its own guardrails: one express visit in flight at a
/// time, a spacing floor between consecutive express fetches, failures feeding
/// the shared backoff signals through the shared pipeline, and RecordFetchNow
/// so the scheduled lane re-spaces around every express fetch.
/// </summary>
public sealed class ExpressVisitRunner(
    IDbContextFactory<PokemonDbContext> dbFactory,
    CardVisitor visitor,
    PoliteGate gate,
    TimeProvider time,
    IOptions<ScraperOptions> options,
    CrawlMetrics metrics,
    CancellationToken applicationStopping,
    ILogger<ExpressVisitRunner> logger)
{
    private readonly SemaphoreSlim _singleFlight = new(1, 1);

    private readonly Lock _sync = new();

    /// <summary>Timestamp of the last express fetch; 0 = never.</summary>
    private long _lastExpressFetch;

    private (long CardId, Task<ExpressResult> Result)? _inFlight;

    /// <summary>The caller's disconnect abandons the await, never the work: a
    /// coalesced waiter may still be listening, and a half-done visit helps
    /// nobody. The visit itself runs on the worker's own lifetime plus the
    /// express timeout.</summary>
    public Task<ExpressResult> RunAsync(long cardId, CancellationToken callerDisconnected)
    {
        Task<ExpressResult> visit;
        bool coalesced;
        lock (_sync)
        {
            if (_inFlight is { } inFlight && inFlight.CardId == cardId)
            {
                // A double-clicked refresh button is the expected caller:
                // both requests ride one fetch and both hear the answer.
                visit = inFlight.Result;
                coalesced = true;
            }
            else
            {
                visit = RunExclusiveAsync(cardId);
                var registered = (cardId, visit);
                _inFlight = registered;
                _ = visit.ContinueWith(
                    completed =>
                    {
                        _ = completed.Exception;
                        lock (_sync)
                        {
                            if (_inFlight == registered)
                            {
                                _inFlight = null;
                            }
                        }
                    },
                    TaskScheduler.Default);
                coalesced = false;
            }
        }

        return AwaitAsync(visit, coalesced, callerDisconnected);
    }

    private static async Task<ExpressResult> AwaitAsync(
        Task<ExpressResult> visit, bool coalesced, CancellationToken callerDisconnected)
    {
        var result = await visit.WaitAsync(callerDisconnected);
        return coalesced && result is ExpressCompleted completed
            ? completed with { Coalesced = true }
            : result;
    }

    private async Task<ExpressResult> RunExclusiveAsync(long cardId)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(applicationStopping);
        limit.CancelAfter(TimeSpan.FromSeconds(options.Value.ExpressTimeoutSeconds));
        var ct = limit.Token;

        try
        {
            await _singleFlight.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return Interrupted();
        }

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var card = await db.Cards.FirstOrDefaultAsync(c => c.Id == cardId, ct);
            if (card is null)
            {
                return new ExpressUnknownCard();
            }

            if (card.NotACardAt is not null)
            {
                // Revisiting a permanent verdict is pure waste, and would
                // re-raise the not-a-card alert over a settled question.
                // Delisted and benched cards ARE visitable here: express is
                // exactly how an operator asks "is it back?".
                return new ExpressNotACard();
            }

            // The express path's own floor. The polite gate is deliberately
            // not consulted — but back-to-back express fetches still keep a
            // courteous distance from each other.
            long last;
            lock (_sync)
            {
                last = _lastExpressFetch;
            }

            if (last != 0)
            {
                var remaining = TimeSpan.FromSeconds(options.Value.ExpressSpacingSeconds)
                                - time.GetElapsedTime(last);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, time, ct);
                }
            }

            using var visit = CrawlTracing.Source.StartActivity("card.express_visit");
            visit?.SetTag("card.id", card.Id);
            visit?.SetTag("card.name", card.Name);
            visit?.SetTag("url.path", card.Url);
            using var scope = logger.BeginScope("Express visit {CardUrl}", card.Url);

            var started = time.GetTimestamp();
            lock (_sync)
            {
                _lastExpressFetch = started;
            }

            // The site just heard from us outside the gate; the lane's next
            // turn re-spaces from this instant.
            gate.RecordFetchNow();

            var result = await visitor.VisitAsync(db, card, visit, "express", ct);
            var duration = time.GetElapsedTime(started);
            metrics.RecordExpressVisit(OutcomeTag(result.Outcome), duration);
            return new ExpressCompleted(result, duration, Coalesced: false);
        }
        catch (OperationCanceledException)
        {
            return Interrupted();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Express visit of card {CardId} failed unexpectedly", cardId);
            metrics.RecordExpressVisit("error", TimeSpan.Zero);
            return new ExpressErrored("unexpected failure; see the worker log");
        }
        finally
        {
            _singleFlight.Release();
        }

        ExpressResult Interrupted()
        {
            if (applicationStopping.IsCancellationRequested)
            {
                metrics.RecordExpressVisit("error", TimeSpan.Zero);
                return new ExpressErrored("worker shutting down");
            }

            metrics.RecordExpressVisit("timeout", TimeSpan.Zero);
            return new ExpressTimedOut();
        }
    }

    internal static string OutcomeTag(VisitOutcome outcome) => outcome switch
    {
        VisitOutcome.Parsed => "parsed",
        VisitOutcome.HttpError => "http_error",
        VisitOutcome.ParseFailed => "parse_failed",
        VisitOutcome.NotACard => "not_a_card",
        _ => "unknown",
    };
}
