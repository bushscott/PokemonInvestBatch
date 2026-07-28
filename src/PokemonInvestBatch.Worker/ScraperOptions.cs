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

    /// <summary>HTML captures of never-before-seen page shapes, for parser fixes.</summary>
    public string ShapeArchiveDirectory { get; init; } = "shapes";

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

    public int CanaryIntervalHours { get; init; } = 6;

    public int ImageIntervalMinutes { get; init; } = 60;

    /// <summary>How long a lane sleeps after the three-strike pause before probing again.</summary>
    public int PauseCooldownMinutes { get; init; } = 30;

    /// <summary>Parse-failure fraction over the last 100 detail visits that trips the alarm.</summary>
    public double ParseFailureAlertThreshold { get; init; } = 0.05;
}
