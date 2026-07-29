namespace PokemonInvestBatch.Application.Scheduling;

/// <summary>
/// The retry queue (dashboard wording; "quarantine" in code and columns):
/// a card whose page repeatedly fails on its own account (parse drift, 404)
/// must not wedge the crawl — without this it stays the stalest card and is
/// re-picked every cycle, burning the entire polite budget on one URL.
/// Three consecutive card-attributable failures earn an exponential time-out
/// with a scheduled comeback date; one success clears everything.
/// </summary>
public static class QuarantinePolicy
{
    private const int Strikes = 3;

    private static readonly TimeSpan BaseSentence = TimeSpan.FromDays(1);

    private static readonly TimeSpan MaxSentence = TimeSpan.FromDays(30);

    /// <summary>Client errors are the card's fault; 429 and 5xx are the site's,
    /// and the AIMD pause owns those — an outage must not convict innocents.</summary>
    public static bool IsCardAttributable(int httpStatus) =>
        httpStatus is >= 400 and < 500 and not 429;

    /// <summary>Null below the strike threshold; then 1d doubling per strike,
    /// capped so a delisted card settles into a cheap monthly probe.</summary>
    public static DateTimeOffset? QuarantineUntil(int failureStreak, DateTimeOffset now)
    {
        if (failureStreak < Strikes)
        {
            return null;
        }

        // 2^5 days already exceeds the cap; clamping here also keeps the
        // TimeSpan arithmetic overflow-free for absurd streaks.
        var doublings = Math.Min(failureStreak - Strikes, 5);
        var sentence = BaseSentence * Math.Pow(2, doublings);
        return now + (sentence < MaxSentence ? sentence : MaxSentence);
    }
}
