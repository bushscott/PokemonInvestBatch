namespace PokemonInvestBatch.Application.Scheduling;

/// <summary>
/// Last line of defence against the poison-card livelock. A visit that dies
/// with an exception nobody anticipated records no visit and no strike, so
/// the card stays the scheduler's top pick and is retried forever. This
/// breaker watches for the same card failing consecutively: environmental
/// trouble (database down, network gone) fails whatever the lane touches
/// next, but one card failing alone, over and over, is card-shaped — so
/// after <c>trippingStreak</c> failures in a row the lane records an
/// ordinary strike and the quarantine machinery takes over.
/// Pure state — no clock, no I/O.
/// </summary>
public sealed class SameCardFailureBreaker(int trippingStreak = 3)
{
    private long _cardId;
    private int _streak;

    /// <summary>
    /// Records an unexpected failure for a card; true when the same card has
    /// now failed enough times in a row that it should be struck. Stays true
    /// on every further failure so repeated strikes escalate the quarantine.
    /// </summary>
    public bool RecordUnexpectedFailure(long cardId)
    {
        if (cardId != _cardId)
        {
            _cardId = cardId;
            _streak = 0;
        }

        return ++_streak >= trippingStreak;
    }

    /// <summary>Any visit that completes without throwing clears the streak.</summary>
    public void Reset()
    {
        _cardId = 0;
        _streak = 0;
    }
}
