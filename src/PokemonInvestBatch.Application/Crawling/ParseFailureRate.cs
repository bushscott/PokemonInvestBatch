namespace PokemonInvestBatch.Application.Crawling;

/// <summary>
/// Telling "a few odd pages" apart from "the site changed its markup".
///
/// Individual parse failures are normal and are already handled per card by
/// quarantine. The alarmable event is the corpus-wide one: a run of failures
/// across unrelated cards means the parser, not the cards, is what broke — and
/// while it is broken the crawl writes nothing, silently.
///
/// The sample floor is the load-bearing half of this rule. A fresh database,
/// or a worker minutes after a restart, can show a 100% failure rate off three
/// observations; alerting on that trains everyone to ignore the alert.
/// </summary>
public static class ParseFailureRate
{
    /// <summary>Fewer observations than this and the rate means nothing.</summary>
    public const int MinimumSamples = 20;

    /// <summary>True when the failure fraction of the recent window is above
    /// the threshold, and the window is big enough for that to mean anything.</summary>
    public static bool IsSpike(int parseFailures, int observed, double threshold)
    {
        if (observed < MinimumSamples)
        {
            return false;
        }

        return (double)parseFailures / observed > threshold;
    }
}
