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
    private readonly Histogram<double> _visitDuration;

    private double _queueStalenessDays;

    public CrawlMetrics(AdaptiveDelay delay)
    {
        Meter = new Meter(MeterName);

        _requests = Meter.CreateCounter<long>("crawl.requests", description: "Requests to pricecharting.com by lane and status");
        _pagesParsed = Meter.CreateCounter<long>("crawl.pages_parsed", description: "Detail pages parsed and written");
        _parseFailures = Meter.CreateCounter<long>("crawl.parse_failures", description: "Pages refused for schema drift");
        _rowsAppended = Meter.CreateCounter<long>("crawl.rows_appended", description: "History rows appended, by kind");
        _cardsVisited = Meter.CreateCounter<long>("crawl.cards_visited", description: "Card detail visits completed");
        _canaryFailures = Meter.CreateCounter<long>("crawl.canary_failures", description: "Canary assertion failures, by path");
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

    public void RecordCanaryFailure(string path) =>
        _canaryFailures.Add(1, new KeyValuePair<string, object?>("path", path));

    /// <summary>Set each scheduler pick from the stalest candidate observed.</summary>
    public void SetQueueStaleness(TimeSpan staleness) => _queueStalenessDays = staleness.TotalDays;

    public void Dispose() => Meter.Dispose();
}
