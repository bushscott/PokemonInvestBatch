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

    // Refreshed by the stats sweep; gauges so restarts cannot skew them the
    // way summed delta counters would.
    private long _corpusSize;
    private long _corpusVisited;
    private long _imagesPending;
    private long _totalPriceRows;
    private long _totalPopulationRows;
    private long _totalSaleRows;
    private long _cardsAtCap;
    private long _cardsQuarantinedNow;
    private long _setsTotal;
    private double _worstCaseDays;
    private long _cardsAtRisk;

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
            "crawl.consecutive_failures", () => (long)delay.ConsecutiveFailures,
            description: "Strikes toward the three-strike pause; early warning the pause boolean can't give");
        Meter.CreateObservableGauge(
            "crawl.queue_staleness_days", () => _queueStalenessDays,
            description: "Staleness of the oldest card the scheduler saw");
        Meter.CreateObservableGauge(
            "crawl.sets_pending", () => _setsPendingWalk,
            description: "Sets awaiting their card walk; zero is the only healthy steady state");
        Meter.CreateObservableGauge(
            "crawl.corpus_size", () => _corpusSize,
            description: "Cards known to exist");
        Meter.CreateObservableGauge(
            "crawl.corpus_visited", () => _corpusVisited,
            description: "Cards visited at least once — coverage numerator");
        Meter.CreateObservableGauge(
            "crawl.images_pending", () => _imagesPending,
            description: "Images discovered but not yet fetched");
        Meter.CreateObservableGauge(
            "crawl.cards_at_risk", () => _cardsAtRisk,
            description: "Cards selling fast enough that their sales outlive the normal rotation only via fast-tracking — the leading indicator of missed sales");
        Meter.CreateObservableGauge(
            "crawl.worst_case_days", () => _worstCaseDays,
            description: "Age of the most-overdue card's data: days since its last visit, or 9999 while any known card has never been visited at all");
        Meter.CreateObservableGauge(
            "crawl.sets_total", () => _setsTotal,
            description: "Sets known to exist — the denominator that grows when enumeration discovers a new set");
        Meter.CreateObservableGauge(
            "crawl.cards_at_cap", () => _cardsAtCap,
            description: "Cards with a sale bucket at cap — the scheduler's hard-override watchlist");
        Meter.CreateObservableGauge(
            "crawl.cards_quarantined_now", () => _cardsQuarantinedNow,
            description: "Cards currently benched; the counter shows events, this shows the standing population");
        Meter.CreateObservableGauge(
            "crawl.total_rows", () =>
            new[]
            {
                new Measurement<long>(_totalPriceRows, new KeyValuePair<string, object?>("kind", "price")),
                new Measurement<long>(_totalPopulationRows, new KeyValuePair<string, object?>("kind", "population")),
                new Measurement<long>(_totalSaleRows, new KeyValuePair<string, object?>("kind", "sale")),
            },
            description: "History rows in Postgres, by kind — the number that only goes up");

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

    /// <summary>Refreshed by the stats sweep each interval.</summary>
    public void SetCorpusStats(long corpusSize, long corpusVisited, long imagesPending, long setsTotal)
    {
        _corpusSize = corpusSize;
        _corpusVisited = corpusVisited;
        _imagesPending = imagesPending;
        _setsTotal = setsTotal;
    }

    /// <summary>Refreshed by the stats sweep each interval. 9999 is the
    /// never-visited sentinel — worst case is unbounded, not measurable.</summary>
    public void SetWorstCaseDays(double days) => _worstCaseDays = days;

    /// <summary>Refreshed by the stats sweep each interval.</summary>
    public void SetCardsAtRisk(long cards) => _cardsAtRisk = cards;

    /// <summary>Refreshed by the stats sweep each interval.</summary>
    public void SetSchedulerStats(long cardsAtCap, long quarantinedNow)
    {
        _cardsAtCap = cardsAtCap;
        _cardsQuarantinedNow = quarantinedNow;
    }

    /// <summary>Refreshed by the stats sweep each interval.</summary>
    public void SetTotalRows(long prices, long populations, long sales)
    {
        _totalPriceRows = prices;
        _totalPopulationRows = populations;
        _totalSaleRows = sales;
    }

    public void Dispose() => Meter.Dispose();
}
