using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Application.Telemetry;

namespace PokemonInvestBatch.Application.Tests.Telemetry;

public class CrawlMetricsTests
{
    private static (CrawlMetrics Metrics, AdaptiveDelay Delay) NewMetrics()
    {
        var delay = new AdaptiveDelay(new AdaptiveDelayOptions());
        return (new CrawlMetrics(delay), delay);
    }

    [Fact]
    public void Requests_count_by_lane_and_status()
    {
        var (metrics, _) = NewMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "crawl.requests");

        metrics.RecordRequest(lane: "detail", statusCode: 200);
        metrics.RecordRequest(lane: "detail", statusCode: 429);

        var measurements = collector.GetMeasurementSnapshot();
        Assert.Equal(2, measurements.Count);
        Assert.Equal("detail", measurements[0].Tags["lane"]);
        Assert.Equal(429, measurements[1].Tags["status"]);
    }

    [Fact]
    public void Rows_appended_count_by_kind()
    {
        var (metrics, _) = NewMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "crawl.rows_appended");

        metrics.RecordRowsAppended(prices: 68, populations: 15, sales: 410);

        var byKind = collector.GetMeasurementSnapshot()
            .ToDictionary(m => (string)m.Tags["kind"]!, m => m.Value);
        Assert.Equal(68, byKind["price"]);
        Assert.Equal(15, byKind["population"]);
        Assert.Equal(410, byKind["sale"]);
    }

    [Fact]
    public void Delay_gauge_tracks_the_live_controller()
    {
        var (metrics, delay) = NewMetrics();
        using var collector = new MetricCollector<double>(metrics.Meter, "crawl.delay_seconds");

        collector.RecordObservableInstruments();
        Assert.Equal(300, collector.LastMeasurement!.Value);

        delay.RecordSuccess(TimeSpan.FromMilliseconds(100));
        collector.RecordObservableInstruments();
        Assert.Equal(150, collector.LastMeasurement!.Value);
    }

    [Fact]
    public void Lane_paused_gauge_reflects_three_strike_state()
    {
        var (metrics, delay) = NewMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "crawl.lane_paused");

        collector.RecordObservableInstruments();
        Assert.Equal(0, collector.LastMeasurement!.Value);

        delay.RecordFailure();
        delay.RecordFailure();
        delay.RecordFailure();
        collector.RecordObservableInstruments();
        Assert.Equal(1, collector.LastMeasurement!.Value);
    }

    [Fact]
    public void Consecutive_failures_gauge_climbs_with_strikes_and_resets_on_success()
    {
        var (metrics, delay) = NewMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "crawl.consecutive_failures");

        delay.RecordFailure();
        delay.RecordFailure();
        collector.RecordObservableInstruments();
        Assert.Equal(2, collector.LastMeasurement!.Value);

        delay.RecordSuccess(TimeSpan.FromMilliseconds(100));
        collector.RecordObservableInstruments();
        Assert.Equal(0, collector.LastMeasurement!.Value);
    }

    [Fact]
    public void Queue_staleness_gauge_reports_what_the_scheduler_last_saw()
    {
        var (metrics, _) = NewMetrics();
        using var collector = new MetricCollector<double>(metrics.Meter, "crawl.queue_staleness_days");

        metrics.SetQueueStaleness(TimeSpan.FromDays(11.5));
        collector.RecordObservableInstruments();

        Assert.Equal(11.5, collector.LastMeasurement!.Value);
    }

    [Fact]
    public void Visit_durations_feed_a_histogram_for_percentiles()
    {
        var (metrics, _) = NewMetrics();
        using var collector = new MetricCollector<double>(metrics.Meter, "crawl.visit_duration_seconds");

        metrics.RecordVisitDuration(TimeSpan.FromSeconds(1.5));

        Assert.Equal(1.5, Assert.Single(collector.GetMeasurementSnapshot()).Value);
    }

    [Fact]
    public void Corpus_gauges_report_the_last_stats_sweep()
    {
        var (metrics, _) = NewMetrics();
        using var size = new MetricCollector<long>(metrics.Meter, "crawl.corpus_size");
        using var visited = new MetricCollector<long>(metrics.Meter, "crawl.corpus_visited");
        using var images = new MetricCollector<long>(metrics.Meter, "crawl.images_pending");
        using var sets = new MetricCollector<long>(metrics.Meter, "crawl.sets_total");

        metrics.SetCorpusStats(corpusSize: 100_000, corpusVisited: 34_000, imagesPending: 250, setsTotal: 303);
        size.RecordObservableInstruments();
        visited.RecordObservableInstruments();
        images.RecordObservableInstruments();
        sets.RecordObservableInstruments();

        Assert.Equal(100_000, size.LastMeasurement!.Value);
        Assert.Equal(34_000, visited.LastMeasurement!.Value);
        Assert.Equal(250, images.LastMeasurement!.Value);
        Assert.Equal(303, sets.LastMeasurement!.Value);
    }

    [Fact]
    public void Scheduler_gauges_report_watchlist_and_bench_sizes()
    {
        var (metrics, _) = NewMetrics();
        using var atCap = new MetricCollector<long>(metrics.Meter, "crawl.cards_at_cap");
        using var benched = new MetricCollector<long>(metrics.Meter, "crawl.cards_quarantined_now");
        using var delisted = new MetricCollector<long>(metrics.Meter, "crawl.cards_delisted");
        using var gone = new MetricCollector<long>(metrics.Meter, "crawl.cards_gone");

        metrics.SetSchedulerStats(cardsAtCap: 12, quarantinedNow: 3, delisted: 1, gone: 2);
        atCap.RecordObservableInstruments();
        benched.RecordObservableInstruments();
        delisted.RecordObservableInstruments();
        gone.RecordObservableInstruments();

        Assert.Equal(12, atCap.LastMeasurement!.Value);
        Assert.Equal(3, benched.LastMeasurement!.Value);
        Assert.Equal(1, delisted.LastMeasurement!.Value);
        Assert.Equal(2, gone.LastMeasurement!.Value);
    }

    [Fact]
    public void Cards_at_risk_gauge_reports_the_stats_sweep_count()
    {
        var (metrics, _) = NewMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "crawl.cards_at_risk");

        metrics.SetCardsAtRisk(["Charizard #4 /game/pokemon-base-set/charizard-4", "Blastoise #2 /game/pokemon-base-set/blastoise-2"]);
        collector.RecordObservableInstruments();

        Assert.Equal(2, collector.LastMeasurement!.Value);
    }

    [Fact]
    public void Each_at_risk_card_is_named_so_the_alert_can_say_who()
    {
        var (metrics, _) = NewMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "crawl.card_at_risk");

        metrics.SetCardsAtRisk(["Charizard #4 /game/pokemon-base-set/charizard-4"]);
        collector.RecordObservableInstruments();

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(1, measurement.Value);
        Assert.Equal(
            "Charizard #4 /game/pokemon-base-set/charizard-4",
            measurement.Tags.Single(t => t.Key == "card").Value);
    }

    [Fact]
    public void A_recovered_card_reports_zero_once_then_falls_silent()
    {
        var (metrics, _) = NewMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "crawl.card_at_risk");

        metrics.SetCardsAtRisk(["Charizard #4 /game/pokemon-base-set/charizard-4"]);

        // Next sweep: recovered. The card must report a 0 so its open alert
        // incident closes on data instead of a loss-of-signal timeout.
        metrics.SetCardsAtRisk([]);
        collector.RecordObservableInstruments();
        var zero = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(0, zero.Value);

        // The sweep after that: gone entirely.
        metrics.SetCardsAtRisk([]);
        collector.Clear();
        collector.RecordObservableInstruments();
        Assert.Empty(collector.GetMeasurementSnapshot());
    }

    [Fact]
    public void Worst_case_days_gauge_reports_the_stats_sweep_verdict()
    {
        var (metrics, _) = NewMetrics();
        using var collector = new MetricCollector<double>(metrics.Meter, "crawl.worst_case_days");

        metrics.SetWorstCaseDays(9999);
        collector.RecordObservableInstruments();

        Assert.Equal(9999, collector.LastMeasurement!.Value);
    }

    [Fact]
    public void Total_rows_gauge_reports_by_kind()
    {
        var (metrics, _) = NewMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "crawl.total_rows");

        metrics.SetTotalRows(prices: 1_500_000, populations: 40_000, sales: 900_000);
        collector.RecordObservableInstruments();

        var byKind = collector.GetMeasurementSnapshot()
            .ToDictionary(m => (string)m.Tags["kind"]!, m => m.Value);
        Assert.Equal(1_500_000, byKind["price"]);
        Assert.Equal(40_000, byKind["population"]);
        Assert.Equal(900_000, byKind["sale"]);
    }

    [Fact]
    public void Quarantines_count_by_reason()
    {
        var (metrics, _) = NewMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "crawl.cards_quarantined");

        metrics.RecordCardQuarantined(reason: "parse");

        var m = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(1, m.Value);
        Assert.Equal("parse", m.Tags["reason"]);
    }

    [Fact]
    public void Monotonicity_violations_feed_a_corpus_wide_counter()
    {
        // One violation is market noise; the counter exists so New Relic can
        // see a corpus-wide step change — the signature of a silent tier remap.
        var (metrics, _) = NewMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "crawl.monotonicity_violations");

        metrics.RecordMonotonicityViolations(2);

        Assert.Equal(2, collector.GetMeasurementSnapshot().Sum(m => m.Value));
    }

    [Fact]
    public void Pop_anomalies_count_by_grader_and_kind()
    {
        var (metrics, _) = NewMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "crawl.pop_anomalies");

        metrics.RecordPopAnomaly(grader: "psa", kind: "spike");

        var m = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(1, m.Value);
        Assert.Equal("psa", m.Tags["grader"]);
        Assert.Equal("spike", m.Tags["kind"]);
    }

    [Fact]
    public void Canary_failures_count_by_path()
    {
        var (metrics, _) = NewMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "crawl.canary_failures");

        metrics.RecordCanaryFailure("/game/pokemon-base-set/charizard-4");

        var m = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal(1, m.Value);
        Assert.Equal("/game/pokemon-base-set/charizard-4", m.Tags["path"]);
    }
}
