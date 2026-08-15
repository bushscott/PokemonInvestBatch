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

    /// <summary>The intake API's bind address. Loopback by default and on
    /// purpose: the API trusts its callers, so only this machine may be one.</summary>
    public string IntakeAddress { get; init; } = "127.0.0.1";

    /// <summary>Port the intake API (refresh requests + express visits) listens on.</summary>
    public int IntakePort { get; init; } = 5155;

    // Scheduling knobs (safety fractions, rate thresholds, the 30-day floor)
    // do NOT live here. VisitPriorityOptions is bound from this same "Scraper"
    // configuration section in Program.cs, so the keys sit beside these in
    // appsettings but the defaults, the rationale, and the code all have one
    // owner in Application.Scheduling. This record once carried a hand-synced
    // copy of two of its four knobs; the other two silently took defaults.

    /// <summary>Rows of overlap at or below which a bucket counts as a near
    /// miss — the page came back almost entirely new, so one more day of
    /// silence would have rolled it. Zero overlap is not a near miss but an
    /// actual loss, which <see cref="PokemonInvestBatch.Application.Scheduling.SalesObservation"/>
    /// reports separately as a capped tier.</summary>
    public int NearMissMargin { get; init; } = 8;

    /// <summary>TCGdex API root, used once per mirror pin (ADR-0009).</summary>
    public string TcgdexBaseUrl { get; init; } = "https://api.tcgdex.net";

    /// <summary>Where the TCGdex per-set JSON mirror lives. The directory IS
    /// the version pin: enrichment joins against these files only, and
    /// refreshing means deleting the directory so the next sweep
    /// re-fetches.</summary>
    public string TcgdexMirrorDirectory { get; init; } = "tcgdex-mirror";

    /// <summary>Hand-curated set aliases (PriceCharting slug → TCGdex set
    /// ids) for names exact matching cannot bridge; kept in the repo like
    /// blacklist.json and re-read every sweep.</summary>
    public string TcgdexSetAliasesPath { get; init; } = "tcgdex-set-aliases.json";

    /// <summary>Cadence of the enrichment sweep. Daily is generous: inputs
    /// only move when enumeration discovers cards (weekly) or the operator
    /// re-pins the mirror, and a no-change sweep writes nothing.</summary>
    public int TcgdexEnrichmentIntervalHours { get; init; } = 24;

    /// <summary>Raw-content base for the pinned PokéAPI dataset (ADR-0011).
    /// The pin is the path segment — bump it to refresh, then delete the
    /// mirror directory.</summary>
    public string PokeapiDataBaseUrl { get; init; } =
        "https://raw.githubusercontent.com/PokeAPI/api-data/";

    /// <summary>Raw-content base for the pinned PokéAPI sprites repo
    /// (ADR-0011), same refresh mechanic as <see cref="PokeapiDataBaseUrl"/>.</summary>
    public string PokeapiSpritesBaseUrl { get; init; } =
        "https://raw.githubusercontent.com/PokeAPI/sprites/";

    /// <summary>Commit SHA of PokeAPI/api-data this Pokédex was built from.</summary>
    public string PokeapiDataPin { get; init; } = "2cda0b56a3a8ad2529d8aac73528225f96d2c848";

    /// <summary>Commit SHA of PokeAPI/sprites the icons come from.</summary>
    public string PokeapiSpritesPin { get; init; } = "c10459b9b0129eaca5c5d9b1cac65336debb1d08";

    /// <summary>Where the pinned PokéAPI dataset mirror lives. Same
    /// directory-is-the-version-pin convention as <see cref="TcgdexMirrorDirectory"/>.</summary>
    public string PokedexMirrorDirectory { get; init; } = "pokeapi-mirror";

    /// <summary>Where fetched species icons land, one file per national dex
    /// number, beside the card image corpus rather than inside it.</summary>
    public string SpeciesIconDirectory { get; init; } = "species-icons";

    /// <summary>Cadence of the Pokédex lane's sweep (mirror fetch/import,
    /// icon fetch, card tagging). Daily is generous for the same reason as
    /// <see cref="TcgdexEnrichmentIntervalHours"/>: a no-change sweep writes
    /// nothing.</summary>
    public int PokedexTaggingIntervalHours { get; init; } = 24;

    /// <summary>Hand-curated TCGdex series→era mapping (ADR-0011), same
    /// user-input posture as <see cref="TcgdexSetAliasesPath"/>. Reserved by
    /// this option now; unused until the set-details sweep later in the
    /// Pokédex phase reads it.</summary>
    public string TcgdexSeriesEraPath { get; init; } = "tcgdex-series-eras.json";
}
