using System.Globalization;
using System.Text.Json;

namespace PokemonInvestBatch.Infrastructure.Pokedex;

/// <summary>
/// One fetch of the pinned PokéAPI dataset mirror (ADR-0011): the pin it was
/// fetched from, when, and how many files it wrote. <see cref="PokeapiMirror.FetchAsync"/>
/// returns it and writes it to disk last; <see cref="PokeapiMirror.Version"/>
/// reads it back.
/// </summary>
public sealed record PokeapiMirrorManifest
{
    /// <summary>Commit SHA of <c>PokeAPI/api-data</c> this mirror was
    /// fetched from (<c>ScraperOptions.PokeapiDataPin</c>) — the directory's
    /// version, the same role <c>TcgdexMirror.Manifest.Version</c> plays,
    /// but exact rather than a nice-to-have/fallback pair: a commit SHA is
    /// already a precise version with nothing to fall back from.</summary>
    public required string Pin { get; init; }

    public required DateTimeOffset FetchedAt { get; init; }

    public required int FileCount { get; init; }
}

/// <summary>
/// The pinned local copy of PokéAPI's species dataset that
/// <c>PokeapiDataset.Load</c> reads (ADR-0011). Same directory-is-the-
/// version-pin convention as <see cref="PokemonInvestBatch.Infrastructure.Enrichment.TcgdexMirror"/>
/// (ADR-0009): one fetch writes every file the Pokédex lane needs plus a
/// manifest, every sweep after that reads only disk, and refreshing means an
/// operator bumps <c>ScraperOptions.PokeapiDataPin</c> and deletes the
/// directory so the next sweep re-fetches from the new commit. The join
/// never takes a live dependency on PokéAPI.
///
/// Upstream (<c>PokeAPI/api-data</c> via <c>raw.githubusercontent.com</c>) is
/// nested — one subdirectory per id, <c>{resource}/{n}/index.json</c> — and
/// this mirror flattens on save to exactly what <c>PokeapiDataset.Load</c>
/// expects: <c>pokemon-species/{n}.json</c>, <c>pokemon/{id}.json</c>,
/// <c>evolution-chain/{id}.json</c> and <c>egg-group/{id}.json</c>, all
/// directly under the mirror directory. The species list itself
/// (<c>pokemon-species/index.json</c>) is fetched to learn which ids exist
/// and is never written to disk — writing it into <c>pokemon-species/</c>
/// would leave a stray file that <c>PokeapiDataset.Load</c>'s <c>*.json</c>
/// directory scan would try to parse as a species and fail on.
///
/// A fetch is all-or-nothing. Any non-200 response, any shape this parser
/// does not recognise — including a paginated or miscounted species list,
/// not only a malformed species file — or a cancelled fetch deletes
/// whatever the fetch had written so far before the exception propagates:
/// a partial mirror is worse than none, because <see cref="Exists"/> would read a
/// half-written directory as a complete one and the lane would import a
/// half-world. The manifest is written last for the same reason — its
/// presence is the only thing <see cref="Exists"/> and the lane trust.
/// </summary>
public static class PokeapiMirror
{
    private const string ManifestFile = "pokeapi-mirror.manifest.json";
    private const string SpeciesDirectory = "pokemon-species";
    private const string PokemonDirectory = "pokemon";
    private const string EvolutionChainDirectory = "evolution-chain";
    private const string EggGroupDirectory = "egg-group";

    /// <summary>PokéAPI has held egg groups at exactly ids 1–15 since
    /// generation 1 — <c>PokedexMaps.EggGroupDisplay</c> maps the same 15
    /// names. Fetched by id range rather than discovered from a list
    /// endpoint: unlike species, this vocabulary is small and closed, and
    /// every id in range is real at the pinned commit.</summary>
    private const int EggGroupCount = 15;

    /// <summary>Spacing between mirror requests. raw.githubusercontent.com
    /// is a CDN, not the polite-gated pricecharting.com — but the fetch
    /// still touches roughly 2,900 small files in one run, so a small delay
    /// keeps it obviously considerate without stretching a one-time
    /// bootstrap past a few minutes.</summary>
    public static readonly TimeSpan FetchSpacing = TimeSpan.FromMilliseconds(50);

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    public static bool Exists(string directory) => File.Exists(Path.Combine(directory, ManifestFile));

    /// <summary>The pin this directory was fetched from, read back from its
    /// manifest — what import rows carry as provenance.</summary>
    public static string Version(string directory) => ReadManifest(directory).Pin;

    /// <summary>Fetches the species list, every species file, each species'
    /// default-variety pokemon file, every distinct evolution chain, and the
    /// 15 egg-group files, flattening upstream's nested layout on save. On
    /// any failure — HTTP, shape, or cancellation — deletes whatever this
    /// call had written and rethrows unchanged, so the caller sees the real
    /// exception and the next run starts from nothing rather than a
    /// half-world.</summary>
    public static async Task<PokeapiMirrorManifest> FetchAsync(
        HttpClient http, string baseUrl, string pin, string directory, TimeProvider time, CancellationToken ct)
    {
        try
        {
            return await FetchCoreAsync(http, baseUrl, pin, directory, time, ct);
        }
        catch (Exception)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }

            throw;
        }
    }

    private static async Task<PokeapiMirrorManifest> FetchCoreAsync(
        HttpClient http, string baseUrl, string pin, string directory, TimeProvider time, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var root = $"{baseUrl}{pin}/data/api/v2";
        Directory.CreateDirectory(Path.Combine(directory, SpeciesDirectory));
        Directory.CreateDirectory(Path.Combine(directory, PokemonDirectory));
        Directory.CreateDirectory(Path.Combine(directory, EvolutionChainDirectory));
        Directory.CreateDirectory(Path.Combine(directory, EggGroupDirectory));

        var fileCount = 0;
        var chainIds = new List<int>();
        var seenChains = new HashSet<int>();

        var speciesIds = await FetchSpeciesListAsync(http, root, ct);
        foreach (var speciesId in speciesIds)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(FetchSpacing, time, ct);
            var speciesFile = $"{SpeciesDirectory}/{speciesId}.json";
            var speciesJson = await FetchStringAsync(http, $"{root}/{SpeciesDirectory}/{speciesId}/index.json", ct);
            await WriteAsync(directory, SpeciesDirectory, speciesId, speciesJson, ct);
            fileCount++;

            int pokemonId;
            int chainId;
            using (var species = JsonDocument.Parse(speciesJson))
            {
                pokemonId = DefaultVarietyId(species.RootElement, speciesFile);
                chainId = EvolutionChainId(species.RootElement, speciesFile);
            }

            if (seenChains.Add(chainId))
            {
                chainIds.Add(chainId);
            }

            await Task.Delay(FetchSpacing, time, ct);
            var pokemonJson = await FetchStringAsync(http, $"{root}/{PokemonDirectory}/{pokemonId}/index.json", ct);
            await WriteAsync(directory, PokemonDirectory, pokemonId, pokemonJson, ct);
            fileCount++;
        }

        // Deduplicated: many species (every Eeveelution, every fossil pair)
        // share one chain, and each is fetched at most once regardless of
        // how many species named it.
        foreach (var chainId in chainIds)
        {
            await Task.Delay(FetchSpacing, time, ct);
            var chainJson = await FetchStringAsync(http, $"{root}/{EvolutionChainDirectory}/{chainId}/index.json", ct);
            await WriteAsync(directory, EvolutionChainDirectory, chainId, chainJson, ct);
            fileCount++;
        }

        for (var eggGroupId = 1; eggGroupId <= EggGroupCount; eggGroupId++)
        {
            await Task.Delay(FetchSpacing, time, ct);
            var eggGroupJson = await FetchStringAsync(http, $"{root}/{EggGroupDirectory}/{eggGroupId}/index.json", ct);
            await WriteAsync(directory, EggGroupDirectory, eggGroupId, eggGroupJson, ct);
            fileCount++;
        }

        var manifest = new PokeapiMirrorManifest
        {
            Pin = pin,
            FetchedAt = time.GetUtcNow(),
            FileCount = fileCount,
        };
        await File.WriteAllTextAsync(
            Path.Combine(directory, ManifestFile), JsonSerializer.Serialize(manifest, ManifestJson), ct);
        return manifest;
    }

    /// <summary>The full species list from <c>pokemon-species/index.json</c>
    /// — every id it names, parsed from each entry's trailing-numeric
    /// <c>url</c>. Read once, in memory, and never written to disk: it is
    /// not part of the flat layout <c>PokeapiDataset.Load</c> reads.
    ///
    /// Refuses loudly on the two drift shapes a re-pin could turn up: a
    /// non-null <c>next</c> (spot-checked live against the pinned commit as
    /// a single unpaginated document — a future pin that paginated would
    /// otherwise fetch only page one and still "succeed," gating the whole
    /// species catalog down silently) and a parsed id count that disagrees
    /// with the document's own <c>count</c> (the two are read from the same
    /// response two different ways, so a mismatch means one of them is
    /// wrong and neither can be trusted). Every other field this fetcher
    /// touches already refuses on unexpected shape; this is that same
    /// posture applied to the one response that sizes everything else.</summary>
    private static async Task<IReadOnlyList<int>> FetchSpeciesListAsync(
        HttpClient http, string root, CancellationToken ct)
    {
        const string file = "pokemon-species/index.json";
        var json = await FetchStringAsync(http, $"{root}/{SpeciesDirectory}/index.json", ct);
        using var document = JsonDocument.Parse(json);

        var next = Require(document.RootElement, "next", file);
        if (next.ValueKind != JsonValueKind.Null)
        {
            throw new InvalidOperationException(
                $"{file}: field 'next' is {next.GetRawText()}, not null — this list is paginated and the " +
                "fetcher reads only one page; refusing the mirror rather than silently importing a partial " +
                "species catalog.");
        }

        var declaredCount = RequireInt(document.RootElement, "count", file);

        var ids = new List<int>();
        foreach (var entry in Require(document.RootElement, "results", file).EnumerateArray())
        {
            ids.Add(ParseTrailingId(RequireString(entry, "url", file), file, "results[].url"));
        }

        if (ids.Count != declaredCount)
        {
            throw new InvalidOperationException(
                $"{file}: field 'count' says {declaredCount} but 'results' held {ids.Count} entries — " +
                "refusing the mirror.");
        }

        return ids;
    }

    private static async Task<string> FetchStringAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static Task WriteAsync(string directory, string subdirectory, int id, string json, CancellationToken ct)
        => File.WriteAllTextAsync(Path.Combine(directory, subdirectory, $"{id}.json"), json, ct);

    /// <summary>The id of the species' default variety (<c>varieties[]</c>
    /// entry with <c>is_default: true</c>) — the <c>pokemon/{id}.json</c>
    /// this mirror fetches for it. Always read from <c>varieties[]</c>
    /// rather than assumed equal to the species id: the fixture sample
    /// checked confirms the two ids match for every species it covers, but
    /// that is not a proof it holds dataset-wide.</summary>
    private static int DefaultVarietyId(JsonElement species, string file)
    {
        foreach (var variety in Require(species, "varieties", file).EnumerateArray())
        {
            if (RequireBool(variety, "is_default", file))
            {
                return ParseTrailingId(
                    RequireString(Require(variety, "pokemon", file), "url", file), file, "varieties[].pokemon.url");
            }
        }

        throw new InvalidOperationException($"{file}: no default variety in 'varieties' — refusing the mirror.");
    }

    private static int EvolutionChainId(JsonElement species, string file)
        => ParseTrailingId(
            RequireString(Require(species, "evolution_chain", file), "url", file), file, "evolution_chain.url");

    /// <summary>Extracts the trailing numeric id from a PokéAPI resource url
    /// ("/api/v2/pokemon-species/133/" → 133) — the same shape
    /// <c>PokeapiDataset</c> parses independently on the Application side.
    /// Duplicated rather than shared on purpose, matching how
    /// <c>TcgdexMirror</c> keeps its own minimal parsing self-contained
    /// rather than reaching into another layer for it.</summary>
    private static int ParseTrailingId(string url, string file, string field)
    {
        var trimmed = url.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        var idText = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;

        return int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new InvalidOperationException(
                $"{file}: field '{field}' ('{url}') does not end in a numeric id — refusing the mirror.");
    }

    private static PokeapiMirrorManifest ReadManifest(string directory) =>
        JsonSerializer.Deserialize<PokeapiMirrorManifest>(
                File.ReadAllText(Path.Combine(directory, ManifestFile)))
            ?? throw new InvalidOperationException($"The PokéAPI mirror manifest in '{directory}' is empty.");

    private static JsonElement Require(JsonElement element, string property, string file) =>
        element.TryGetProperty(property, out var value)
            ? value
            : throw new InvalidOperationException(
                $"{file}: missing field '{property}' — refusing the mirror.");

    private static string RequireString(JsonElement element, string property, string file) =>
        Require(element, property, file).GetString() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{file}: field '{property}' is empty or null — refusing the mirror.");

    private static bool RequireBool(JsonElement element, string property, string file) =>
        Require(element, property, file).GetBoolean();

    private static int RequireInt(JsonElement element, string property, string file) =>
        Require(element, property, file).GetInt32();
}
