using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Worker.Intake;

/// <summary>
/// The loopback-only HTTP surface for sibling apps on this machine: the
/// queued refresh request and the synchronous express visit. Trust comes
/// from the bind address (127.0.0.1), not from auth. Routes speak card ids
/// because the id is the one name both sides already share through the
/// database. All decisions live in RefreshRequestIntake / ExpressVisitRunner
/// and the pure Respond mappers below; the endpoints only translate.
/// </summary>
public static class IntakeApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/healthz", () => Results.Text("ok"));

        app.MapPost(
            "/cards/{cardId:long}/refresh-request",
            async (long cardId, RefreshRequestIntake intake, CancellationToken ct) =>
            {
                var (status, body) = Respond(cardId, await intake.FileAsync(cardId, ct));
                return Results.Json(body, statusCode: status);
            });

        app.MapPost(
            "/cards/{cardId:long}/express-visit",
            async (long cardId, ExpressVisitRunner runner, HttpContext http) =>
            {
                var (status, body) = Respond(cardId, await runner.RunAsync(cardId, http.RequestAborted));
                return Results.Json(body, statusCode: status);
            });
    }

    internal static (int Status, object Body) Respond(long cardId, RefreshRequestReceipt receipt) => receipt.Outcome switch
    {
        RefreshRequestOutcome.Accepted or RefreshRequestOutcome.AlreadyPending => (StatusCodes.Status202Accepted, new
        {
            cardId,
            requestedAt = receipt.RequestedAt,
            alreadyPending = receipt.Outcome == RefreshRequestOutcome.AlreadyPending,
            quarantinedUntil = receipt.QuarantinedUntil,
        }),
        RefreshRequestOutcome.UnknownCard => (StatusCodes.Status404NotFound, Error(cardId, "unknown card")),
        RefreshRequestOutcome.NotACard => (StatusCodes.Status409Conflict, Error(cardId, "not a card")),
        _ => (StatusCodes.Status409Conflict, Error(cardId, "delisted")),
    };

    internal static (int Status, object Body) Respond(long cardId, ExpressResult result) => result switch
    {
        ExpressUnknownCard => (StatusCodes.Status404NotFound, Error(cardId, "unknown card")),
        ExpressNotACard => (StatusCodes.Status409Conflict, Error(cardId, "not a card")),
        ExpressErrored errored => (StatusCodes.Status500InternalServerError, Error(cardId, errored.Reason)),
        ExpressCompleted completed => (completed.Visit.Outcome switch
        {
            // 200: fresh data committed. 502: the upstream site failed us, not
            // the caller. 422: we fetched a page and refused it — parse drift,
            // or the page proved to be no card at all and was retired.
            VisitOutcome.Parsed => StatusCodes.Status200OK,
            VisitOutcome.HttpError => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status422UnprocessableEntity,
        }, new
        {
            cardId,
            outcome = ExpressVisitRunner.OutcomeTag(completed.Visit.Outcome),
            upstreamStatus = completed.Visit.HttpStatus,
            durationMs = (long)completed.Duration.TotalMilliseconds,
            coalesced = completed.Coalesced,
        }),
        _ => (StatusCodes.Status500InternalServerError, Error(cardId, "unhandled express result")),
    };

    private static object Error(long cardId, string error) => new { cardId, error };
}
