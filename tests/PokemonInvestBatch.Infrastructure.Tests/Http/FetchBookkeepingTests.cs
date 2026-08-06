using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Application.Telemetry;
using PokemonInvestBatch.Infrastructure.Http;

namespace PokemonInvestBatch.Infrastructure.Tests.Http;

/// <summary>
/// One helper, every lane: these prove that ANY caller of RecordOutcome gets
/// the full routing — including the 429/503-to-ceiling rule that was once
/// fixed only in the detail lane's private copy.
/// </summary>
public class FetchBookkeepingTests
{
    private static (CrawlMetrics Metrics, AdaptiveDelay Delay) NewLedger()
    {
        var delay = new AdaptiveDelay(new AdaptiveDelayOptions());
        return (new CrawlMetrics(delay), delay);
    }

    private static FetchResult Fetch(int status, string? html = null, TimeSpan? retryAfter = null) =>
        html is null
            ? new FetchFailure
            {
                StatusCode = status,
                Latency = TimeSpan.FromMilliseconds(200),
                RetryAfter = retryAfter,
            }
            : new FetchedPage
            {
                StatusCode = status,
                Html = html,
                Latency = TimeSpan.FromMilliseconds(200),
                RetryAfter = retryAfter,
            };

    private static void TightenToFloor(AdaptiveDelay delay)
    {
        while (delay.Current > TimeSpan.FromSeconds(10))
        {
            delay.RecordSuccess(TimeSpan.FromMilliseconds(200));
        }
    }

    [Fact]
    public void A_success_tightens_the_delay()
    {
        var (metrics, delay) = NewLedger();
        using var _ = metrics;

        Fetch(200, html: "<html/>").RecordOutcome(metrics, delay, "spot check");

        Assert.Equal(TimeSpan.FromSeconds(150), delay.Current);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(0)]
    public void A_site_failure_doubles_the_delay(int status)
    {
        var (metrics, delay) = NewLedger();
        using var _ = metrics;
        TightenToFloor(delay);

        Fetch(status).RecordOutcome(metrics, delay, "spot check");

        Assert.Equal(TimeSpan.FromSeconds(20), delay.Current);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(404)]
    public void A_broken_url_is_the_cards_problem_not_the_sites(int status)
    {
        // The starvation loop this rule ends: a delisted card's bench
        // recheck 302s every ~25 minutes, and each one re-doubled the
        // site-wide delay to the ceiling faster than successes could
        // claw it back — one dead card throttled the crawl 35x.
        var (metrics, delay) = NewLedger();
        using var _ = metrics;
        TightenToFloor(delay);

        Fetch(status).RecordOutcome(metrics, delay, "detail");

        Assert.Equal(TimeSpan.FromSeconds(10), delay.Current);
    }

    [Fact]
    public void Broken_urls_never_trip_the_site_trouble_pause()
    {
        var (metrics, delay) = NewLedger();
        using var _ = metrics;
        TightenToFloor(delay);

        for (var i = 0; i < 3; i++)
        {
            Fetch(302).RecordOutcome(metrics, delay, "detail");
        }

        Assert.False(delay.ShouldPause);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public void Rate_limiting_jumps_straight_to_the_ceiling_from_any_lane(int status)
    {
        // The drift this helper exists to end: only the detail lane used to
        // know that 429/503 mean "stop", not "slow down a little".
        var (metrics, delay) = NewLedger();
        using var _ = metrics;
        TightenToFloor(delay);

        Fetch(status).RecordOutcome(metrics, delay, "set catalog");

        Assert.Equal(TimeSpan.FromSeconds(300), delay.Current);
    }

    [Fact]
    public void A_retry_after_beyond_the_ceiling_is_honoured()
    {
        var (metrics, delay) = NewLedger();
        using var _ = metrics;

        Fetch(429, retryAfter: TimeSpan.FromSeconds(600)).RecordOutcome(metrics, delay, "set catalog");

        Assert.Equal(TimeSpan.FromSeconds(600), delay.Current);
    }

    [Fact]
    public void Every_outcome_counts_the_request_under_its_lane_tag()
    {
        var (metrics, delay) = NewLedger();
        using var _ = metrics;
        using var collector = new MetricCollector<long>(metrics.Meter, "crawl.requests");

        Fetch(200, html: "<html/>").RecordOutcome(metrics, delay, "spot check");
        Fetch(429).RecordOutcome(metrics, delay, "set catalog");

        var measurements = collector.GetMeasurementSnapshot();
        Assert.Equal(2, measurements.Count);
        Assert.Equal("spot check", measurements[0].Tags["lane"]);
        Assert.Equal(429, measurements[1].Tags["status"]);
    }
}
