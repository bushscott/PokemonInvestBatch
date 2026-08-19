using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PokemonInvestBatch.Application.Enrichment;

namespace PokemonInvestBatch.Infrastructure.Enrichment;

/// <summary>
/// The pinned local copy of one locale of TCGdex's catalog that enrichment
/// joins against (ADR-0009: en; the Japanese shelf adds a ja mirror), and
/// that the Pokédex phase's set-details sweep (ADR-0011) reuses rather than
/// mirroring separately. One directory holds one locale. The directory IS
/// the version pin: one fetch writes every per-set JSON plus a manifest,
/// every sweep after that reads only disk, and refreshing is the operator
/// deleting the directory so the next sweep re-fetches. The join never
/// takes a live dependency on the API.
///
/// Two lanes — EnrichmentLane and PokedexLane — share this one mirror, so
/// ensuring it exists is coordinated (<see cref="EnsureAsync"/>) rather than
/// left to each caller's own Exists-then-fetch check.
///
/// Loading is strict on the fields the join computes from: id, name,
/// serie.id, serie.name, releaseDate, cardCount.official and
/// cardCount.total on every set, plus id/localId/name on every entry of a
/// set's cards[] array when that (optional) array is present. A shape this
/// code does not understand refuses loudly rather than enriching from a
/// guess — the same posture the page parsers take toward drift.
/// </summary>
public static class TcgdexMirror
{
    private const string ManifestFile = "manifest.json";
    private const string SetsDirectory = "sets";

    /// <summary>Spacing between mirror requests. TCGdex publishes no hard
    /// limits and asks for consideration; ~220 requests once per pin is the
    /// entire footprint, and one second apart keeps it obviously polite.</summary>
    public static readonly TimeSpan FetchSpacing = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = true };

    /// <summary>Serializes first-fetches of this mirror directory across the
    /// two lanes that share it (EnrichmentLane and PokedexLane). Both run as
    /// <c>BackgroundService</c>s inside one process — one systemd unit
    /// (ADR-0006) — so this gate only has to coordinate within that process;
    /// there is no cross-process story to solve.
    ///
    /// Why it exists: without it, a fresh box starting both lanes within
    /// milliseconds of each other, or an operator deleting the directory to
    /// force a refresh on a live system (the very thing this class's own log
    /// line invites — "delete the directory to refresh"), can start two
    /// concurrent fetches into the same directory. <see cref="FetchAsync"/>
    /// does not delete its output on failure, and two writers can interleave
    /// writes to the same set file on Linux — worst case both "succeed," a
    /// manifest lands, <see cref="Exists"/> reports true forever, and one
    /// corrupted set file makes every future <see cref="LoadAsync"/> throw,
    /// recoverable only by an operator manually deleting the directory.
    /// Concurrent first-boot and post-delete refresh are exactly the two
    /// scenarios <see cref="EnsureAsync"/> closes.</summary>
    private static readonly SemaphoreSlim EnsureGate = new(1, 1);

    public sealed record Manifest
    {
        public required DateTimeOffset FetchedAt { get; init; }

        /// <summary>tcgdex/cards-database's newest release tag at fetch time,
        /// when GitHub answered — the human-meaningful half of the pin.</summary>
        public string? ReleaseTag { get; init; }

        public required int SetCount { get; init; }

        /// <summary>Which TCGdex locale this directory mirrors. Null on
        /// manifests written before the mirror learned locales — those are
        /// all English, so read null as "en".</summary>
        public string? Locale { get; init; }

        /// <summary>What enrichment rows carry as provenance.</summary>
        public string Version =>
            ReleaseTag is { Length: > 0 } tag ? tag : $"api-{FetchedAt:yyyy-MM-dd}";
    }

    public static bool Exists(string directory) => File.Exists(Path.Combine(directory, ManifestFile));

    /// <summary>The coordinated way to ensure a mirror exists at
    /// <paramref name="directory"/> — what EnrichmentLane and PokedexLane
    /// both call instead of an <see cref="Exists"/>-then-<see cref="FetchAsync"/>
    /// check of their own, so the race two lanes sharing one mirror creates
    /// is closed in exactly one place (see <see cref="EnsureGate"/> for why
    /// it is needed at all).
    ///
    /// Takes <paramref name="newHttpClient"/> — a factory delegate — rather
    /// than an already-built <see cref="HttpClient"/> deliberately: a caller
    /// passing a pre-built client has already paid for
    /// <c>IHttpClientFactory.CreateClient</c> (an eager-argument-evaluation
    /// trap C# does not warn about) before this method gets a chance to
    /// decide anything, which defeats the point of the steady-state fast path
    /// below — every sweep after the mirror's first fetch would still
    /// construct and immediately discard a client. Calling
    /// <paramref name="newHttpClient"/> here, only in the branch that is
    /// actually about to fetch, keeps "no mirror work needed" genuinely free.
    /// A plain delegate rather than <c>IHttpClientFactory</c> itself because
    /// this project (Infrastructure) does not otherwise depend on
    /// <c>Microsoft.Extensions.Http</c> — the caller (a Worker lane, which
    /// does) supplies <c>() =&gt; httpFactory.CreateClient(name)</c>.
    ///
    /// Double-checked: an unlocked fast-path <see cref="Exists"/> check first
    /// — steady state, never touches <see cref="EnsureGate"/> or
    /// <paramref name="newHttpClient"/> at all — and only when that finds no
    /// mirror does it acquire the gate and check again before fetching. A
    /// caller that loses the race sees the winner's already-completed mirror
    /// on its post-acquire check and returns having fetched nothing itself.
    /// Logs only when this call is the one that actually fetches — the
    /// race's loser logs nothing, since it did nothing.</summary>
    public static async Task EnsureAsync(
        Func<HttpClient> newHttpClient,
        string baseUrl,
        string locale,
        string directory,
        TimeProvider time,
        ILogger log,
        CancellationToken ct)
    {
        if (Exists(directory))
        {
            return;
        }

        await EnsureGate.WaitAsync(ct);
        try
        {
            if (Exists(directory))
            {
                // Lost the race: a concurrent caller already fetched while
                // this one waited for the gate.
                return;
            }

            log.LogInformation(
                "No TCGdex {Locale} mirror at {Directory} — fetching one (the pin; delete the directory to refresh)",
                locale,
                directory);
            var fetched = await FetchAsync(newHttpClient(), baseUrl, locale, directory, time, ct);
            log.LogInformation(
                "Mirrored {Sets} TCGdex {Locale} sets as version {Version}",
                fetched.SetCount,
                locale,
                fetched.Version);
        }
        finally
        {
            EnsureGate.Release();
        }
    }

    /// <summary>Fetch one locale's whole catalog into the directory. Written
    /// set-by-set with the manifest last, so an interrupted fetch leaves no
    /// manifest and the next sweep simply fetches again. Public for its own
    /// direct tests below and for <see cref="EnsureAsync"/>, which is what
    /// production code should call instead — a bare Exists-then-FetchAsync
    /// pair has none of <see cref="EnsureAsync"/>'s coordination against a
    /// concurrent caller doing the same thing.</summary>
    public static async Task<Manifest> FetchAsync(
        HttpClient http, string baseUrl, string locale, string directory, TimeProvider time, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(directory, SetsDirectory));

        using var listResponse = await http.GetAsync($"{baseUrl}/v2/{locale}/sets", ct);
        listResponse.EnsureSuccessStatusCode();
        var setIds = new List<string>();
        using (var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(ct)))
        {
            foreach (var entry in list.RootElement.EnumerateArray())
            {
                setIds.Add(RequireString(entry, "id", "set list"));
            }
        }

        foreach (var id in setIds)
        {
            ct.ThrowIfCancellationRequested();
            // Set ids become file names; anything that could escape the
            // directory is a shape we refuse, not sanitize.
            if (id.Length == 0 || id.Contains('/') || id.Contains('\\') || id.Contains(".."))
            {
                throw new InvalidOperationException(
                    $"TCGdex set id '{id}' is not a safe file name — refusing the mirror.");
            }

            await Task.Delay(FetchSpacing, time, ct);
            using var setResponse = await http.GetAsync($"{baseUrl}/v2/{locale}/sets/{Uri.EscapeDataString(id)}", ct);
            setResponse.EnsureSuccessStatusCode();
            await File.WriteAllTextAsync(
                Path.Combine(directory, SetsDirectory, $"{id}.json"),
                await setResponse.Content.ReadAsStringAsync(ct),
                ct);
        }

        var manifest = new Manifest
        {
            FetchedAt = time.GetUtcNow(),
            ReleaseTag = await TryFetchReleaseTagAsync(http, ct),
            SetCount = setIds.Count,
            Locale = locale,
        };
        await File.WriteAllTextAsync(
            Path.Combine(directory, ManifestFile), JsonSerializer.Serialize(manifest, ManifestJson), ct);
        return manifest;
    }

    public static async Task<(TcgdexCatalog Catalog, Manifest Manifest)> LoadAsync(
        string directory, CancellationToken ct)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(
                await File.ReadAllTextAsync(Path.Combine(directory, ManifestFile), ct))
            ?? throw new InvalidOperationException($"The TCGdex mirror manifest in '{directory}' is empty.");

        var sets = new List<TcgdexSet>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(directory, SetsDirectory), "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            sets.Add(ParseSet(await File.ReadAllTextAsync(file, ct), Path.GetFileName(file)));
        }

        if (sets.Count != manifest.SetCount)
        {
            throw new InvalidOperationException(
                $"The TCGdex mirror in '{directory}' holds {sets.Count} sets but its manifest says " +
                $"{manifest.SetCount} — delete the directory to re-fetch.");
        }

        return (new TcgdexCatalog(sets), manifest);
    }

    private static TcgdexSet ParseSet(string json, string source)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var cardCount = Require(root, "cardCount", source);
        var cards = new List<TcgdexCard>();
        if (root.TryGetProperty("cards", out var cardsElement))
        {
            foreach (var card in cardsElement.EnumerateArray())
            {
                cards.Add(new TcgdexCard
                {
                    Id = RequireString(card, "id", source),
                    LocalId = RequireString(card, "localId", source),
                    Name = RequireString(card, "name", source),
                });
            }
        }

        // Required on purpose: serie is the digital-set exclusion, and a set
        // whose serie we cannot read is a shape we refuse rather than
        // classify by guesswork — the parsers' posture toward drift, applied
        // here. Both id and name live on the same object and are equally
        // reliable in the live catalog (verified 2026-08-15 across Base,
        // Gym, Neo, E-Card, EX, Diamond & Pearl, Platinum, HeartGold &
        // SoulSilver, Black & White, XY, Sun & Moon, Sword & Shield and
        // Scarlet & Violet), so name gets the same strictness id already had.
        var serie = Require(root, "serie", source);

        return new TcgdexSet
        {
            Id = RequireString(root, "id", source),
            Name = RequireString(root, "name", source),
            SerieId = RequireString(serie, "id", source),
            SerieName = RequireString(serie, "name", source),
            // Same live-verified reliability as serie above — every set
            // sampled (1999's Base Set through 2023's Scarlet & Violet)
            // carried releaseDate in yyyy-MM-dd.
            ReleaseDate = RequireDate(root, "releaseDate", source),
            OfficialCount = RequireInt(cardCount, "official", source),
            TotalCount = RequireInt(cardCount, "total", source),
            Cards = cards,
        };
    }

    private static async Task<string?> TryFetchReleaseTagAsync(HttpClient http, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(
                "https://api.github.com/repos/tcgdex/cards-database/releases/latest", ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return release.RootElement.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
        }
        catch (HttpRequestException)
        {
            // The tag is the nice-to-have half of the pin; the fetch date is
            // the dependable half.
            return null;
        }
    }

    private static JsonElement Require(JsonElement element, string property, string source) =>
        element.TryGetProperty(property, out var value)
            ? value
            : throw new InvalidOperationException(
                $"TCGdex data ({source}) carries no '{property}' — refusing to enrich from a shape " +
                "this code does not understand.");

    private static string RequireString(JsonElement element, string property, string source) =>
        Require(element, property, source).GetString() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"TCGdex data ({source}) has an empty '{property}' — refusing to enrich from a shape " +
                "this code does not understand.");

    private static int RequireInt(JsonElement element, string property, string source) =>
        Require(element, property, source).GetInt32();

    private static DateOnly RequireDate(JsonElement element, string property, string source)
    {
        var raw = RequireString(element, property, source);
        return DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new InvalidOperationException(
                $"TCGdex data ({source}) has a '{property}' of '{raw}' that is not a yyyy-MM-dd date — " +
                "refusing to enrich from a shape this code does not understand.");
    }
}
