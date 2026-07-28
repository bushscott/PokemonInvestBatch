using System.Diagnostics.Metrics;
using PokemonInvestBatch.Application.Crawling;

namespace PokemonInvestBatch.Application.Telemetry;

/// <summary>
/// The crawler's dimensional metrics — the signals New Relic alerts on.
/// Plain .NET Meter instruments; the OTLP exporter is wired at the host.
/// </summary>
public sealed class CrawlMetrics : IDisposable
{
    public const string MeterName = "PokemonInvestBatch";

    private readonly Counter<long> _requests;
    private readonly Counter<long> _pagesParsed;
    private readonly Counter<long> _parseFailures;
    private readonly Counter<long> _rowsAppended;
    private readonly Counter<long> _cardsVisited;
    private readonly Counter<long> _canaryFailures;
    private readonly Counter<long> _monotonicityViolations;
    private readonly Counter<long> _popAnomalies;
    private readonly Counter<long> _cardsQuarantined;
    private readonly Histogram<double> _visitDuration;

    private double _queueStalenessDays;

    private int _setsPendingWalk;

    public CrawlMetrics(AdaptiveDelay delay)
    {
        Meter = new Meter(MeterName);

        _requests = Meter.CreateCounter<long>("crawl.requests", description: "Requests to pricecharting.com by lane and status");
        _pagesParsed = Meter.CreateCounter<long>("crawl.pages_parsed", description: "Detail pages parsed and written");
        _parseFailures = Meter.CreateCounter<long>("crawl.parse_failures", description: "Pages refused for schema drift");
        _rowsAppended = Meter.CreateCounter<long>("crawl.rows_appended", description: "History rows appended, by kind");
        _cardsVisited = Meter.CreateCounter<long>("crawl.cards_visited", description: "Card detail visits completed");
        _canaryFailures = Meter.CreateCounter<long>("crawl.canary_failures", description: "Canary assertion failures, by path");
        _monotonicityViolations = Meter.CreateCounter<long>("crawl.monotonicity_violations", description: "Grade-price monotonicity violations; a step change is a silent tier remap");
        _popAnomalies = Meter.CreateCounter<long>("crawl.pop_anomalies", description: "Population cells that spiked or shrank beyond grading pace, by grader and kind");
        _cardsQuarantined = Meter.CreateCounter<long>("crawl.cards_quarantined", description: "Cards benched after repeated card-attributable failures, by reason");
        _visitDuration = Meter.CreateHistogram<double>("crawl.visit_duration_seconds", unit: "s", description: "Card visit wall time, fetch through commit");

        Meter.CreateObservableGauge(
            "crawl.delay_seconds", () => delay.Current.TotalSeconds,
            description: "Current AIMD politeness delay");
        Meter.CreateObservableGauge(
            "crawl.lane_paused", () => delay.ShouldPause ? 1L : 0L,
            description: "1 while the three-strike pause is in force");
        Meter.CreateObservableGauge(
            "crawl.queue_staleness_days", () => _queueStalenessDays,
            description: "Staleness of the oldest card the scheduler saw");
        Meter.CreateObservableGauge(
            "crawl.sets_pending", () => _setsPendingWalk,
            description: "Sets awaiting their card walk; zero is the only healthy steady state");

        // Alarm-bearing counters emit a zero at startup so their series exist
        // from boot — absence is then always detectable (loss-of-signal), and
        // alert conditions validate against real data.
        _pagesParsed.Add(0);
        _parseFailures.Add(0);
        _cardsVisited.Add(0);
        _canaryFailures.Add(0);
        _monotonicityViolations.Add(0);
        _popAnomalies.Add(0);
        _cardsQuarantined.Add(0);
    }

    /// <summary>Exposed for MetricCollector-based tests and host registration.</summary>
    public Meter Meter { get; }

    public void RecordRequest(string lane, int statusCode) =>
        _requests.Add(1, new KeyValuePair<string, object?>("lane", lane), new KeyValuePair<string, object?>("status", statusCode));

    public void RecordPageParsed() => _pagesParsed.Add(1);

    public void RecordParseFailure() => _parseFailures.Add(1);

    public void RecordRowsAppended(int prices, int populations, int sales)
    {
        _rowsAppended.Add(prices, new KeyValuePair<string, object?>("kind", "price"));
        _rowsAppended.Add(populations, new KeyValuePair<string, object?>("kind", "population"));
        _rowsAppended.Add(sales, new KeyValuePair<string, object?>("kind", "sale"));
    }

    public void RecordCardVisited() => _cardsVisited.Add(1);

    /// <summary>Fetch through commit — excludes the polite-gate wait.</summary>
    public void RecordVisitDuration(TimeSpan duration) => _visitDuration.Record(duration.TotalSeconds);

    public void RecordMonotonicityViolations(int count) => _monotonicityViolations.Add(count);

    public void RecordCardQuarantined(string reason) =>
        _cardsQuarantined.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void RecordPopAnomaly(string grader, string kind) =>
        _popAnomalies.Add(1, new KeyValuePair<string, object?>("grader", grader), new KeyValuePair<string, object?>("kind", kind));

    public void RecordCanaryFailure(string path) =>
        _canaryFailures.Add(1, new KeyValuePair<string, object?>("path", path));

    /// <summary>Set each scheduler pick from the stalest candidate observed.</summary>
    public void SetQueueStaleness(TimeSpan staleness) => _queueStalenessDays = staleness.TotalDays;

    /// <summary>Refreshed by the enumeration lane each cycle check.</summary>
    public void SetPendingSets(int pending) => _setsPendingWalk = pending;

    public void Dispose() => Meter.Dispose();
}
