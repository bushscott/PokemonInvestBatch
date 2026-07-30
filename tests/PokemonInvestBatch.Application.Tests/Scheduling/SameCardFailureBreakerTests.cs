using PokemonInvestBatch.Application.Scheduling;

namespace PokemonInvestBatch.Application.Tests.Scheduling;

public class SameCardFailureBreakerTests
{
    [Fact]
    public void Trips_on_the_third_consecutive_failure_of_the_same_card()
    {
        var breaker = new SameCardFailureBreaker();

        Assert.False(breaker.RecordUnexpectedFailure(cardId: 7));
        Assert.False(breaker.RecordUnexpectedFailure(cardId: 7));
        Assert.True(breaker.RecordUnexpectedFailure(cardId: 7));
    }

    [Fact]
    public void Keeps_striking_once_tripped_so_quarantine_escalates()
    {
        var breaker = new SameCardFailureBreaker();
        breaker.RecordUnexpectedFailure(7);
        breaker.RecordUnexpectedFailure(7);
        breaker.RecordUnexpectedFailure(7);

        Assert.True(breaker.RecordUnexpectedFailure(7));
    }

    [Fact]
    public void A_different_card_failing_restarts_the_count()
    {
        // Environmental trouble fails whatever the lane touches next — a
        // changing card id is the signature of a problem that isn't the card.
        var breaker = new SameCardFailureBreaker();
        breaker.RecordUnexpectedFailure(7);
        breaker.RecordUnexpectedFailure(7);

        Assert.False(breaker.RecordUnexpectedFailure(8));
        Assert.False(breaker.RecordUnexpectedFailure(8));
        Assert.True(breaker.RecordUnexpectedFailure(8));
    }

    [Fact]
    public void A_clean_visit_resets_the_streak()
    {
        var breaker = new SameCardFailureBreaker();
        breaker.RecordUnexpectedFailure(7);
        breaker.RecordUnexpectedFailure(7);

        breaker.Reset();

        Assert.False(breaker.RecordUnexpectedFailure(7));
    }
}
