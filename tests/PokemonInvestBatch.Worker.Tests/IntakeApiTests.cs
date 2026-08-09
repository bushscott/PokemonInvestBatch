using System.Text.Json;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.Worker.Intake;
using PokemonInvestBatch.Worker.Lanes;

namespace PokemonInvestBatch.Worker.Tests;

/// <summary>
/// The status-code contract, pinned without a server: the endpoints are
/// one-line translations of these pure mappers, so this is the whole HTTP
/// decision surface.
/// </summary>
public class IntakeApiTests
{
    [Fact]
    public void Accepted_and_already_pending_asks_both_return_202()
    {
        var asked = DateTimeOffset.UtcNow;

        var (acceptedStatus, acceptedBody) = IntakeApi.Respond(
            630417, new RefreshRequestReceipt(RefreshRequestOutcome.Accepted, asked, null));
        var (pendingStatus, pendingBody) = IntakeApi.Respond(
            630417, new RefreshRequestReceipt(RefreshRequestOutcome.AlreadyPending, asked, null));

        Assert.Equal(202, acceptedStatus);
        Assert.Equal(202, pendingStatus);
        Assert.Contains("\"alreadyPending\":false", JsonSerializer.Serialize(acceptedBody));
        Assert.Contains("\"alreadyPending\":true", JsonSerializer.Serialize(pendingBody));
    }

    [Theory]
    [InlineData(RefreshRequestOutcome.UnknownCard, 404)]
    [InlineData(RefreshRequestOutcome.NotACard, 409)]
    [InlineData(RefreshRequestOutcome.Delisted, 409)]
    public void Refused_asks_map_to_their_status_codes(RefreshRequestOutcome outcome, int expected)
    {
        var (status, _) = IntakeApi.Respond(630417, new RefreshRequestReceipt(outcome, null, null));

        Assert.Equal(expected, status);
    }

    [Theory]
    [InlineData(VisitOutcome.Parsed, 200)]
    [InlineData(VisitOutcome.HttpError, 502)]
    [InlineData(VisitOutcome.ParseFailed, 422)]
    [InlineData(VisitOutcome.NotACard, 422)]
    public void Express_visit_outcomes_map_to_their_status_codes(VisitOutcome outcome, int expected)
    {
        var completed = new ExpressCompleted(
            new CardVisitor.VisitResult(outcome, 200), TimeSpan.FromSeconds(2), Coalesced: false);

        var (status, body) = IntakeApi.Respond(630417, completed);

        Assert.Equal(expected, status);
        Assert.Contains("\"durationMs\":2000", JsonSerializer.Serialize(body));
    }

    [Fact]
    public void Express_refusals_map_to_their_status_codes()
    {
        Assert.Equal(404, IntakeApi.Respond(1, new ExpressUnknownCard()).Status);
        Assert.Equal(409, IntakeApi.Respond(1, new ExpressNotACard()).Status);
        Assert.Equal(504, IntakeApi.Respond(1, new ExpressTimedOut()).Status);
        Assert.Equal(500, IntakeApi.Respond(1, new ExpressErrored("boom")).Status);
    }
}
