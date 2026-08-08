namespace PokemonInvestBatch.Worker;

public sealed record ScraperOptions
{
    public string BaseUrl { get; init; } = "https://www.pricecharting.com";

    /// <summary>Goes into every request's User-Agent as the abuse contact.</summary>
    public string ContactEmail { get; init; } = "";

    public string CategoryPath { get; init; } = "/category/pokemon-cards";

    /// <summary>User-maintained JSON array of { slug, reason }; re-read every enumeration cycle.</summary>
    public string BlacklistPath { get; init; } = "blacklist.json";

    public string ImageDirectory { get; init; } = "images";

    /// <summary>HTML captures of never-before-seen page fingerprints, for parser fixes.</summary>
    public string FingerprintArchiveDirectory { get; init; } = "fingerprints";

    /// <summary>Famous, liquid cards asserted hard on a fast cadence.</summary>
    public string[] CanaryPaths { get; init; } =
    [
        "/game/pokemon-base-set/charizard-4",
        "/game/pokemon-base-set/blastoise-2",
        "/game/pokemon-base-set/venusaur-15",
        "/game/pokemon-base-set/pikachu-58",
        "/game/pokemon-jungle/snorlax-11",
    ];

    public int EnumerationIntervalDays { get; init; } = 7;

    /// <summary>Walk abandonment point. The biggest real set (463 products)
    /// fits in 4 pages of 150; a set still offering "next" past this many
    /// pages means the pagination shape changed.</summary>
    public int MaxSetWalkPages { get; init; } = 20;

    public int CanaryIntervalHours { get; init; } = 6;

    public int ImageIntervalMinutes { get; init; } = 60;

    /// <summary>How long a lane sleeps after the three-strike pause before probing again.</summary>
    public int PauseCooldownMinutes { get; init; } = 30;

    /// <summary>Parse-failure fraction over the last 100 detail visits that trips the alarm.</summary>
    public double ParseFailureAlertThreshold { get; init; } = 0.05;

    /// <summary>Cadence of the stats sweep publishing coverage and row-total gauges.</summary>
    public int StatsIntervalMinutes { get; init; } = 5;

    /// <summary>At most one benched-card retry from the retry queue per this many minutes.</summary>
    public int BenchRecheckIntervalMinutes { get; init; } = 10;

    /// <summary>How often the delisted probe wakes to ask one retired card
    /// whether its page came back. Four chances a day is ample headroom for a
    /// bench of dozens on a monthly rotation.</summary>
    public int DelistedProbeIntervalHours { get; init; } = 6;

    /// <summary>How long a retired card rests between probes. Long on purpose:
    /// a delisted page is expected to stay dead, and the whole point of
    /// retiring it was to stop spending the polite budget on it.</summary>
    public int DelistedProbeAgeDays { get; init; } = 30;
}
