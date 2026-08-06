using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Application.Scheduling;
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
    /// not a doubling — no matter which lane hears them. A 3xx/4xx is the
    /// URL's own fault, not the site's, and the site-wide delay must not
    /// hear it at all: quarantine owns broken URLs, and a single dead card
    /// re-doubling the delay on every bench recheck once starved the whole
    /// crawl to the ceiling.</summary>
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
        else if (!QuarantinePolicy.IsCardAttributable(fetched.StatusCode))
        {
            delay.RecordFailure(fetched.RetryAfter);
        }
    }
}
