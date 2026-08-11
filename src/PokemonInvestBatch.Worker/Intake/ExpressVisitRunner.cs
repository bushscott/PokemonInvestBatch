using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Application.Telemetry;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.Worker.Lanes;

namespace PokemonInvestBatch.Worker.Intake;

/// <summary>What an express visit came to, for the endpoint to translate.</summary>
public abstract record ExpressResult;

public sealed record ExpressUnknownCard : ExpressResult;

public sealed record ExpressNotACard : ExpressResult;

public sealed record ExpressErrored(string Reason) : ExpressResult;

public sealed record ExpressCompleted(CardVisitor.VisitResult Visit, TimeSpan Duration, bool Coalesced) : ExpressResult;

/// <summary>
/// The instantaneous path: update this card now, separately from the schedule,
/// while the caller waits. It runs the same errand as the lane (CardVisitor),
/// with the same failure attribution — but skips the pick, the polite gate,
/// and every wait of its own by explicit decision (ADR-0008): a person is
/// waiting on each call, so it fetches the moment it is asked, in parallel
/// with any other express visit, with no floor, no queue, and no timeout.
/// One fetch, once — a failure is reported to the caller, never retried here.
/// What remains: same-card coalescing (which shares a fetch rather than
/// delaying one), failures feeding the shared backoff signals through the
/// shared pipeline, and RecordFetchNow so the scheduled lane re-spaces around
/// every express fetch. Express volume is the calling app's to bound.
/// </summary>
public sealed class ExpressVisitRunner(
    IDbContextFactory<PokemonDbContext> dbFactory,
    CardVisitor visitor,
    PoliteGate gate,
    TimeProvider time,
    CrawlMetrics metrics,
    CancellationToken applicationStopping,
    ILogger<ExpressVisitRunner> logger)
{
    private readonly Lock _sync = new();

    /// <summary>The visit in flight for each card, so concurrent asks for one
    /// card share a fetch. Keyed by card because visits for different cards no
    /// longer wait on each other.</summary>
    private readonly Dictionary<long, Task<ExpressResult>> _inFlight = [];

    /// <summary>The caller's disconnect abandons the await, never the work: a
    /// coalesced waiter may still be listening, and a half-done visit helps
    /// nobody. The visit itself runs on the worker's own lifetime.</summary>
    public Task<ExpressResult> RunAsync(long cardId, CancellationToken callerDisconnected)
    {
        Task<ExpressResult> visit;
        bool coalesced;
        lock (_sync)
        {
            if (_inFlight.TryGetValue(cardId, out var running))
            {
                // A double-clicked refresh button is the expected caller:
                // both requests ride one fetch and both hear the answer.
                visit = running;
                coalesced = true;
            }
            else
            {
                visit = RunVisitAsync(cardId);
                _inFlight[cardId] = visit;
                var registered = visit;
                _ = visit.ContinueWith(
                    completed =>
                    {
                        _ = completed.Exception;
                        lock (_sync)
                        {
                            if (_inFlight.TryGetValue(cardId, out var current) && current == registered)
                            {
                                _inFlight.Remove(cardId);
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

    /// <summary>One fetch, started now. The only cancellation is the worker
    /// shutting down; a slow site is bounded by the HttpClient's own timeout
    /// and comes back as an HTTP failure, not as a wait imposed here.</summary>
    private async Task<ExpressResult> RunVisitAsync(long cardId)
    {
        var ct = applicationStopping;

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

            using var visit = CrawlTracing.Source.StartActivity("card.express_visit");
            visit?.SetTag("card.id", card.Id);
            visit?.SetTag("card.name", card.Name);
            visit?.SetTag("url.path", card.Url);
            using var scope = logger.BeginScope("Express visit {CardUrl}", card.Url);

            var started = time.GetTimestamp();

            // The site just heard from us outside the gate; the lane's next
            // turn re-spaces from this instant.
            gate.RecordFetchNow();

            var result = await visitor.VisitAsync(db, card, visit, "express", ct);
            var duration = time.GetElapsedTime(started);
            metrics.RecordExpressVisit(OutcomeTag(result.Outcome), duration);
            return new ExpressCompleted(result, duration, Coalesced: false);
        }
        catch (OperationCanceledException) when (applicationStopping.IsCancellationRequested)
        {
            metrics.RecordExpressVisit("error", TimeSpan.Zero);
            return new ExpressErrored("worker shutting down");
        }
        catch (Exception e)
        {
            // The caller gets the exception, not a shrug: it is a sibling app
            // on this box, and "see the worker log" is not an answer a page
            // can act on. The visit is not retried — one ask, one fetch.
            logger.LogError(e, "Express visit of card {CardId} failed", cardId);
            metrics.RecordExpressVisit("error", TimeSpan.Zero);
            return new ExpressErrored(Describe(e));
        }
    }

    /// <summary>The exception, in the form a caller can act on. EF wraps the
    /// interesting part — "duplicate key", "connection refused" — inside a
    /// provider exception, so the innermost message rides along.</summary>
    internal static string Describe(Exception e)
    {
        var root = e;
        while (root.InnerException is { } inner)
        {
            root = inner;
        }

        return ReferenceEquals(root, e)
            ? $"{e.GetType().Name}: {e.Message}"
            : $"{e.GetType().Name}: {e.Message} ({root.GetType().Name}: {root.Message})";
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
