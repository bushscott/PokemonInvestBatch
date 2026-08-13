using System.Text.Json;
using PokemonInvestBatch.Application.Enrichment;

namespace PokemonInvestBatch.Infrastructure.Enrichment;

/// <summary>
/// The pinned local copy of TCGdex's English catalog that enrichment joins
/// against (ADR-0009). The directory IS the version pin: one fetch writes
/// every per-set JSON plus a manifest, every sweep after that reads only
/// disk, and refreshing is the operator deleting the directory so the next
/// sweep re-fetches. The join never takes a live dependency on the API.
///
/// Loading is strict on the fields the join computes from (id, localId,
/// name, cardCount.official): a shape this code does not understand refuses
/// loudly rather than enriching from a guess — the same posture the page
/// parsers take toward drift.
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

    public sealed record Manifest
    {
        public required DateTimeOffset FetchedAt { get; init; }

        /// <summary>tcgdex/cards-database's newest release tag at fetch time,
        /// when GitHub answered — the human-meaningful half of the pin.</summary>
        public string? ReleaseTag { get; init; }

        public required int SetCount { get; init; }

        /// <summary>What enrichment rows carry as provenance.</summary>
        public string Version =>
            ReleaseTag is { Length: > 0 } tag ? tag : $"api-{FetchedAt:yyyy-MM-dd}";
    }

    public static bool Exists(string directory) => File.Exists(Path.Combine(directory, ManifestFile));

    /// <summary>Fetch the whole English catalog into the directory. Written
    /// set-by-set with the manifest last, so an interrupted fetch leaves no
    /// manifest and the next sweep simply fetches again.</summary>
    public static async Task<Manifest> FetchAsync(
        HttpClient http, string baseUrl, string directory, TimeProvider time, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(directory, SetsDirectory));

        using var listResponse = await http.GetAsync($"{baseUrl}/v2/en/sets", ct);
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
            using var setResponse = await http.GetAsync($"{baseUrl}/v2/en/sets/{Uri.EscapeDataString(id)}", ct);
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

        return new TcgdexSet
        {
            Id = RequireString(root, "id", source),
            Name = RequireString(root, "name", source),
            // Required on purpose: serie is the digital-set exclusion, and a
            // set whose serie we cannot read is a shape we refuse rather
            // than classify by guesswork — the parsers' posture toward
            // drift, applied here.
            SerieId = RequireString(Require(root, "serie", source), "id", source),
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
}
