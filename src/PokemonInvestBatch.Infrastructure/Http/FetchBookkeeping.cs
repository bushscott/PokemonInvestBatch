using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Application.Telemetry;

namespace PokemonInvestBatch.Infrastructure.Http;

/// <summary>
/// The ritual after every polite fetch, identical for every lane: count the
/// request, then teach the courtesy delay what happened. Centralized because
/// the copies drifted — 429/503 routing was fixed in the detail lane and
/// missed everywhere else, so a rate-limited set walk doubled the delay
/// instead of jumping to the ceiling.
/// </summary>
public static class FetchBookkeeping
{
    /// <summary>429/503 are an explicit "stop" — answered with the ceiling,
    /// not a doubling — no matter which lane hears them.</summary>
    public static void RecordOutcome(
        this FetchResult fetched, CrawlMetrics metrics, AdaptiveDelay delay, string laneTag)
    {
        metrics.RecordRequest(laneTag, fetched.StatusCode);
        if (fetched is FetchedPage)
        {
            delay.RecordSuccess(fetched.Latency);
        }
        else if (fetched.StatusCode is 429 or 503)
        {
            delay.RecordRateLimited(fetched.RetryAfter);
        }
        else
        {
            delay.RecordFailure(fetched.RetryAfter);
        }
    }
}
