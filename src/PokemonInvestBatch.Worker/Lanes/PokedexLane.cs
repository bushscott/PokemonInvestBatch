using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PokemonInvestBatch.Application.Enrichment;
using PokemonInvestBatch.Application.Pokedex;
using PokemonInvestBatch.Infrastructure.Enrichment;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.Infrastructure.Pokedex;

namespace PokemonInvestBatch.Worker.Lanes;

/// <summary>What one Pokédex sweep computed and wrote — the composite of
/// every stage <see cref="PokedexLane.RunSweepAsync"/> runs, and exactly the
/// numbers the Pokédex phase spec §7 calls its acceptance receipts.</summary>
public sealed record PokedexSweepResult
{
    /// <summary>Upsert counts from importing the pinned PokéAPI dataset into
    /// <c>species</c> and its three child tables.</summary>
    public required SpeciesImportResult Species { get; init; }

    /// <summary>Per-tier counts from fetching whichever species icons were
    /// still missing from disk.</summary>
    public required IconFetchResult Icons { get; init; }

    /// <summary>Work-set size, status counts, and junction-row counts from
    /// re-tagging every card whose species link may be stale.</summary>
    public required TaggingSweepResult Tagging { get; init; }

    /// <summary>Matched-vs-pending counts from filling <c>set_details</c>
    /// against the existing TCGdex set map.</summary>
    public required SetDetailsSweepResult SetDetails { get; init; }
}

/// <summary>
/// The Pokédex phase's composition lane (ADR-0011): the one place that runs
/// every stage of the PokéAPI join in order and reports the result as a
/// single receipt. Structurally cloned from <see cref="EnrichmentLane"/> —
/// same loop-with-interval-delay shape, same try/catch log-and-continue
/// posture (a failed sweep is logged and retried next cycle; it must never
/// stop the crawl or enrichment lanes — spec §6's "lane failure" rule), same
/// <see cref="RunSweepAsync"/>-is-the-testable-unit split.
///
/// One sweep, in order:
/// <list type="number">
/// <item><description>Ensure the pinned PokéAPI dataset mirror exists,
/// fetching it if not (<see cref="PokeapiMirror"/>) — a one-time cost per
/// pin; every sweep after the first reads only disk for this
/// step.</description></item>
/// <item><description>Load every species off disk
/// (<see cref="PokeapiDataset.Load"/>).</description></item>
/// <item><description>Upsert them into <c>species</c> and its child tables
/// (<see cref="SpeciesImporter.ImportAsync"/>).</description></item>
/// <item><description>Fetch whichever species icons are still missing
/// (<see cref="SpeciesIconStore.FetchMissingAsync"/>) — cheap after the
/// first full run, since an icon already on disk is skipped with no request
/// at all.</description></item>
/// <item><description>Build the title-match candidate list from the
/// species' English display names
/// (<see cref="SpeciesMatcher.BuildCandidates"/>).</description></item>
/// <item><description>Re-tag every card whose species link may be stale
/// (<see cref="TaggingSweep"/>).</description></item>
/// <item><description>Fill <c>set_details</c> from the existing TCGdex set
/// map (<see cref="SetDetailsSweep"/>) — the catalog and aliases are loaded
/// the same way <see cref="EnrichmentLane.RunSweepAsync"/> loads them for its
/// own join, through the shared <see cref="TcgdexMirror.EnsureAsync"/> both
/// lanes call: the two lanes read one TCGdex mirror rather than each keeping
/// its own copy, and it is <c>EnsureAsync</c>'s own gate — not this lane, not
/// <see cref="EnrichmentLane"/> — that makes two lanes sharing one mirror
/// directory safe against each other.</description></item>
/// <item><description>One structured log line carrying every count above —
/// spec §7's acceptance receipts.</description></item>
/// </list>
///
/// The first sweep after a fresh deploy self-bootstraps: with no PokéAPI
/// mirror on disk yet, step 1 fetches the full ~2,900-file dataset before
/// anything else in the sweep can run. Step 7's TCGdex mirror fetch goes
/// through <see cref="TcgdexMirror.EnsureAsync"/>, the same call
/// <see cref="EnrichmentLane"/> makes for its own mirror use — its in-process
/// gate is what lets two lanes starting within milliseconds of each other on
/// a fresh box, or racing again any time an operator deletes the directory to
/// force a refresh on a live system, share one fetch instead of writing over
/// each other. Every sweep after that reads both mirrors from disk only, and
/// touches the network solely for species icons this instance has never
/// fetched before.
/// </summary>
public sealed class PokedexLane(
    IDbContextFactory<PokemonDbContext> dbFactory,
    IHttpClientFactory httpFactory,
    TimeProvider time,
    IOptions<ScraperOptions> options,
    ILogger<PokedexLane> logger) : BackgroundService
{
    public const string HttpClientName = "pokeapi";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Pokédex sweep failed");
            }

            await Task.Delay(
                TimeSpan.FromHours(options.Value.PokedexTaggingIntervalHours), time, stoppingToken);
        }
    }

    public async Task<PokedexSweepResult> RunSweepAsync(CancellationToken ct)
    {
        var scraper = options.Value;
        var http = httpFactory.CreateClient(HttpClientName);

        if (!PokeapiMirror.Exists(scraper.PokedexMirrorDirectory))
        {
            logger.LogInformation(
                "No PokéAPI mirror at {Directory} — fetching one (pin {Pin}; delete the directory to refresh)",
                scraper.PokedexMirrorDirectory, scraper.PokeapiDataPin);
            var fetched = await PokeapiMirror.FetchAsync(
                http, scraper.PokeapiDataBaseUrl, scraper.PokeapiDataPin, scraper.PokedexMirrorDirectory, time, ct);
            logger.LogInformation(
                "Mirrored {Files} PokéAPI files at pin {Pin}", fetched.FileCount, fetched.Pin);
        }

        var species = PokeapiDataset.Load(scraper.PokedexMirrorDirectory);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var importResult = await SpeciesImporter.ImportAsync(db, species, ct);

        // Every sweep, the full dex list — SpeciesIconStore's own skip-if-
        // exists check is what keeps this cheap after the first full run.
        var dexNumbers = species.Select(s => s.Id).ToList();
        var iconResult = await SpeciesIconStore.FetchMissingAsync(
            http, scraper.PokeapiSpritesBaseUrl, scraper.PokeapiSpritesPin, scraper.SpeciesIconDirectory,
            dexNumbers, logger, ct);

        var candidates = SpeciesMatcher.BuildCandidates(species.Select(s => (s.Id, s.Name)));
        var taggingResult = await new TaggingSweep().RunAsync(db, candidates, time, ct);

        var (catalog, aliases) = await LoadTcgdexCatalogAsync(scraper, ct);
        var setDetailsResult = await new SetDetailsSweep(catalog, aliases, scraper.TcgdexSeriesEraPath)
            .RunAsync(db, ct);

        logger.LogInformation(
            "Pokédex sweep: species {Inserted} inserted, {Updated} updated, {Unchanged} unchanged; "
            + "icons {FromMenuIcons} from-menu, {FromDefaultSprites} from-default, {Skipped} skipped, "
            + "{Missing} missing; tagging {Examined} examined, {Tagged} tagged, {NoSpecies} no-species, "
            + "{Quarantined} quarantined, {LinksWritten} links written, {LinksRemoved} links removed; "
            + "sets {Matched} matched, {Pending} pending",
            importResult.Inserted, importResult.Updated, importResult.Unchanged,
            iconResult.FromMenuIcons, iconResult.FromDefaultSprites, iconResult.Skipped, iconResult.Missing,
            taggingResult.Examined, taggingResult.Tagged, taggingResult.NoSpecies, taggingResult.Quarantined,
            taggingResult.LinksWritten, taggingResult.LinksRemoved,
            setDetailsResult.Matched, setDetailsResult.Pending);

        return new PokedexSweepResult
        {
            Species = importResult,
            Icons = iconResult,
            Tagging = taggingResult,
            SetDetails = setDetailsResult,
        };
    }

    /// <summary>Loads the catalog and alias map <see cref="SetDetailsSweep"/>
    /// needs. The mirror itself is ensured through
    /// <see cref="TcgdexMirror.EnsureAsync"/> — the same call
    /// <see cref="EnrichmentLane.RunSweepAsync"/> makes for its own join — so
    /// this lane never runs its own Exists-then-fetch against the directory
    /// EnrichmentLane also writes to. <c>EnsureAsync</c>'s internal gate is
    /// what makes two lanes sharing one mirror directory safe; nothing local
    /// to this method or this lane does that coordination.</summary>
    private async Task<(TcgdexCatalog Catalog, IReadOnlyDictionary<string, IReadOnlyList<string>> Aliases)>
        LoadTcgdexCatalogAsync(ScraperOptions scraper, CancellationToken ct)
    {
        await TcgdexMirror.EnsureAsync(
            () => httpFactory.CreateClient(EnrichmentLane.HttpClientName),
            scraper.TcgdexBaseUrl,
            "en",
            scraper.TcgdexMirrorDirectory,
            time,
            logger,
            ct);

        // The Japanese shelf's mirror is pinned and topped up from the same
        // sweep, one directory per locale — so the ja documents are already
        // on disk for the ja alias join (ADR-0012).
        await TcgdexMirror.EnsureAsync(
            () => httpFactory.CreateClient(EnrichmentLane.HttpClientName),
            scraper.TcgdexBaseUrl,
            "ja",
            scraper.TcgdexJaMirrorDirectory,
            time,
            logger,
            ct);

        var (catalog, _) = await TcgdexMirror.LoadAsync(scraper.TcgdexMirrorDirectory, ct);

        // Same posture as EnrichmentLane's own read of this file:
        // user-maintained JSON, absent means empty, malformed refuses
        // loudly (a silently dropped alias would quietly unmap a curated set).
        var aliases = File.Exists(scraper.TcgdexSetAliasesPath)
            ? TcgdexSetAliases.Parse(await File.ReadAllTextAsync(scraper.TcgdexSetAliasesPath, ct))
            : new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        return (catalog, aliases);
    }
}
