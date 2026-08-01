namespace PokemonInvestBatch.Application.Scheduling;

/// <summary>
/// A retry queue for cards that keep failing: a card whose page repeatedly
/// fails on its own account (parse drift, 404) must not wedge the crawl —
/// without this it stays the stalest card and is re-picked every cycle,
/// burning the entire polite budget on one URL. Three consecutive
/// card-attributable failures earn an exponential time-out with a scheduled
/// comeback date; one success clears everything.
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

    /// <summary>A benched card earns its early retry this fraction of the way
    /// into its sentence: 1/24th, so a first bench (1 day) is retryable after
    /// an hour, and each further strike doubles that wait with the sentence.</summary>
    private const double SecondChanceFraction = 1.0 / 24;

    /// <summary>Null below the strike threshold; then 1d doubling per strike,
    /// capped so a delisted card settles into a cheap monthly probe.</summary>
    public static DateTimeOffset? QuarantineUntil(int failureStreak, DateTimeOffset now)
    {
        if (failureStreak < Strikes)
        {
            return null;
        }

        return now + Sentence(failureStreak);
    }

    /// <summary>
    /// When this benched card becomes eligible for a second-chance retry —
    /// well before its full sentence lapses, so a fixed problem (ours or the
    /// site's) clears the retry queue in hours, not days. The sentence length
    /// is a pure function of the streak, so the bench start is recoverable
    /// from the comeback date alone.
    /// </summary>
    public static DateTimeOffset SecondChanceAt(int failureStreak, DateTimeOffset quarantinedUntil)
    {
        var sentence = Sentence(failureStreak);
        var benchedAt = quarantinedUntil - sentence;
        return benchedAt + sentence * SecondChanceFraction;
    }

    private static TimeSpan Sentence(int failureStreak)
    {
        // 2^5 days already exceeds the cap; clamping here also keeps the
        // TimeSpan arithmetic overflow-free for absurd streaks.
        var doublings = Math.Clamp(failureStreak - Strikes, 0, 5);
        var sentence = BaseSentence * Math.Pow(2, doublings);
        return sentence < MaxSentence ? sentence : MaxSentence;
    }
}
