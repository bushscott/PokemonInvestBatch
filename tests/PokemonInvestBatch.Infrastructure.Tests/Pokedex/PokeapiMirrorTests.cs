using PokemonInvestBatch.Application.Pokedex;
using PokemonInvestBatch.Infrastructure.Pokedex;

namespace PokemonInvestBatch.Infrastructure.Tests.Pokedex;

public class PokeapiMirrorTests : IDisposable
{
    private const string BaseUrl = "https://pokeapi.example/";
    private const string Pin = "test-pin-0001";

    // Real, trimmed PokéAPI JSON (Task 6's fixtures, copied rather than
    // referenced across projects — see the .csproj's CopyToOutputDirectory
    // item) — three species (Eevee/Umbreon share evolution-chain 67, so the
    // handler proves dedup; Type: Null carries chain 399 alone).
    private static readonly string FixturesDirectory =
        Path.Combine(AppContext.BaseDirectory, "Pokedex", "Fixtures");

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"pokeapi-mirror-{Guid.NewGuid():N}");

    private const string SpeciesListJson = """
        {
          "count": 3,
          "next": null,
          "previous": null,
          "results": [
            { "name": "eevee", "url": "/api/v2/pokemon-species/133/" },
            { "name": "umbreon", "url": "/api/v2/pokemon-species/197/" },
            { "name": "type-null", "url": "/api/v2/pokemon-species/772/" }
          ]
        }
        """;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void A_missing_directory_is_no_mirror()
    {
        Assert.False(PokeapiMirror.Exists(_directory));
    }

    [Fact]
    public void An_empty_directory_is_no_mirror()
    {
        // No manifest = not a mirror, even once the directory itself exists
        // (an interrupted fetch leaves exactly this shape).
        Directory.CreateDirectory(_directory);

        Assert.False(PokeapiMirror.Exists(_directory));
    }

    [Fact]
    public async Task Fetch_flattens_species_pokemon_chains_and_egg_groups_then_writes_the_manifest_last()
    {
        var handler = BuildHandler();
        using var http = new HttpClient(handler);

        var manifest = await PokeapiMirror.FetchAsync(
            http, BaseUrl, Pin, _directory, TimeProvider.System, CancellationToken.None);

        Assert.Equal(Pin, manifest.Pin);
        // 3 species + 3 default-variety pokemon + 2 distinct evolution
        // chains (67 shared by Eevee/Umbreon counts once) + 15 egg groups.
        Assert.Equal(23, manifest.FileCount);
        Assert.True((TimeProvider.System.GetUtcNow() - manifest.FetchedAt).Duration() < TimeSpan.FromMinutes(1));

        Assert.True(PokeapiMirror.Exists(_directory));
        Assert.Equal(Pin, PokeapiMirror.Version(_directory));

        // Flat layout — exactly what PokeapiDataset.Load reads: no
        // per-id subdirectory, no "index.json" leaf.
        AssertFixtureWritten("pokemon-species", "197");
        AssertFixtureWritten("pokemon-species", "133");
        AssertFixtureWritten("pokemon-species", "772");
        AssertFixtureWritten("pokemon", "197");
        AssertFixtureWritten("pokemon", "133");
        AssertFixtureWritten("pokemon", "772");
        AssertFixtureWritten("evolution-chain", "67");
        AssertFixtureWritten("evolution-chain", "399");

        for (var eggGroupId = 1; eggGroupId <= 15; eggGroupId++)
        {
            Assert.True(
                File.Exists(Path.Combine(_directory, "egg-group", $"{eggGroupId}.json")),
                $"egg-group/{eggGroupId}.json was not written.");
        }

        // The species list is fetched to learn which ids exist but is never
        // itself written into pokemon-species/ — Load()'s "*.json" scan of
        // that folder would try to parse it as a species and fail on it.
        Assert.Equal(3, Directory.EnumerateFiles(Path.Combine(_directory, "pokemon-species")).Count());
        Assert.Equal(3, Directory.EnumerateFiles(Path.Combine(_directory, "pokemon")).Count());
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(_directory, "evolution-chain")).Count());
        Assert.Equal(15, Directory.EnumerateFiles(Path.Combine(_directory, "egg-group")).Count());

        // Eevee(133) and Umbreon(197) both name evolution-chain 67 — the
        // fetch must hit it once, not twice.
        handler.CallCounts.TryGetValue(Upstream("evolution-chain/67/index.json"), out var chain67Calls);
        Assert.Equal(1, chain67Calls);

        // Eevee(133) also carries two non-default varieties (10159, 10205),
        // for which the stub has no response. If the fetcher ever asked for
        // one it would 404 and the fetch above would already have thrown.
    }

    [Fact]
    public async Task The_fetched_mirror_loads_cleanly_through_PokeapiDataset()
    {
        // The real cross-task contract: Task 6's reader, pointed at exactly
        // what this fetcher wrote, with nothing hand-adjusted in between.
        using var http = new HttpClient(BuildHandler());
        await PokeapiMirror.FetchAsync(
            http, BaseUrl, Pin, _directory, TimeProvider.System, CancellationToken.None);

        var species = PokeapiDataset.Load(_directory);

        Assert.Equal(new[] { 133, 197, 772 }, species.Select(s => s.Id).OrderBy(id => id));
        var umbreon = species.Single(s => s.Id == 197);
        Assert.Equal("Umbreon", umbreon.Name);
        Assert.Equal(133, umbreon.EvolvesFrom);
        Assert.Equal(new[] { "Dark" }, umbreon.Types);
    }

    [Fact]
    public async Task A_non_200_response_fails_loudly_and_deletes_whatever_was_already_written()
    {
        // egg-group/10 is reached only after every species, pokemon and
        // evolution-chain file — nine egg-group files too — have already
        // landed on disk, so this proves real content gets removed, not
        // just an empty shell.
        var failingUrl = Upstream("egg-group/10/index.json");
        var handler = BuildHandler(notFoundUrl: failingUrl);
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => PokeapiMirror.FetchAsync(
            http, BaseUrl, Pin, _directory, TimeProvider.System, CancellationToken.None));

        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task A_paginated_species_list_refuses_loudly_and_deletes_the_directory()
    {
        // A future re-pin could point at a paginated response; fetching
        // only page one and still writing a manifest would silently halve
        // the catalog with nothing to say so — the exact "half-world that
        // looks complete" failure the class doc says this design prevents.
        // Never observed at the current pin (spot-checked live — see the
        // task report), but must refuse loudly if it ever is.
        const string paginatedListJson = """
            {
              "count": 1025,
              "next": "https://pokeapi.example/test-pin-0001/data/api/v2/pokemon-species/?offset=20&limit=20",
              "previous": null,
              "results": [
                { "name": "eevee", "url": "/api/v2/pokemon-species/133/" }
              ]
            }
            """;
        using var http = new HttpClient(HandlerServingList(paginatedListJson));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PokeapiMirror.FetchAsync(
            http, BaseUrl, Pin, _directory, TimeProvider.System, CancellationToken.None));

        Assert.Contains("pokemon-species/index.json", ex.Message);
        Assert.Contains("next", ex.Message);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task A_species_list_whose_declared_count_disagrees_with_its_results_refuses_loudly_and_deletes_the_directory()
    {
        // 'count' and 'results' are read from the same response two
        // different ways; a mismatch means the document itself is
        // inconsistent and neither number can be trusted to size the
        // catalog this fetch is about to build.
        const string miscountedListJson = """
            {
              "count": 3,
              "next": null,
              "previous": null,
              "results": [
                { "name": "eevee", "url": "/api/v2/pokemon-species/133/" },
                { "name": "umbreon", "url": "/api/v2/pokemon-species/197/" }
              ]
            }
            """;
        using var http = new HttpClient(HandlerServingList(miscountedListJson));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => PokeapiMirror.FetchAsync(
            http, BaseUrl, Pin, _directory, TimeProvider.System, CancellationToken.None));

        Assert.Contains("pokemon-species/index.json", ex.Message);
        Assert.Contains("'count' says 3 but 'results' held 2 entries", ex.Message);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task A_cancelled_fetch_deletes_whatever_the_directory_already_held_and_leaves_no_manifest()
    {
        // Stands in for cancellation observed mid-fetch: real content
        // already on disk before the token registers as cancelled.
        Directory.CreateDirectory(Path.Combine(_directory, "pokemon-species"));
        await File.WriteAllTextAsync(Path.Combine(_directory, "pokemon-species", "133.json"), "{}");

        using var http = new HttpClient(BuildHandler());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => PokeapiMirror.FetchAsync(
            http, BaseUrl, Pin, _directory, TimeProvider.System, cts.Token));

        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public async Task Version_reads_the_pin_back_from_a_hand_written_manifest()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "pokeapi-mirror.manifest.json"),
            """{ "Pin": "2cda0b56a3a8ad2529d8aac73528225f96d2c848", "FetchedAt": "2026-08-14T00:00:00+00:00", "FileCount": 2900 }""");

        Assert.Equal("2cda0b56a3a8ad2529d8aac73528225f96d2c848", PokeapiMirror.Version(_directory));
    }

    private void AssertFixtureWritten(string resource, string id)
    {
        var written = Path.Combine(_directory, resource, $"{id}.json");
        Assert.True(File.Exists(written), $"{resource}/{id}.json was not written.");
        Assert.Equal(Fixture(resource, id), File.ReadAllText(written));
    }

    private static StubHandler BuildHandler(string? notFoundUrl = null)
    {
        var responses = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [Upstream("pokemon-species/index.json")] = SpeciesListJson,
            [Upstream("pokemon-species/133/index.json")] = Fixture("pokemon-species", "133"),
            [Upstream("pokemon-species/197/index.json")] = Fixture("pokemon-species", "197"),
            [Upstream("pokemon-species/772/index.json")] = Fixture("pokemon-species", "772"),
            [Upstream("pokemon/133/index.json")] = Fixture("pokemon", "133"),
            [Upstream("pokemon/197/index.json")] = Fixture("pokemon", "197"),
            [Upstream("pokemon/772/index.json")] = Fixture("pokemon", "772"),
            [Upstream("evolution-chain/67/index.json")] = Fixture("evolution-chain", "67"),
            [Upstream("evolution-chain/399/index.json")] = Fixture("evolution-chain", "399"),
        };

        // The 15 egg-group files are fetched unconditionally by id range,
        // not discovered from a list — the real egg-group/5.json content is
        // reused at all 15 stub urls since PokeapiMirror never parses this
        // resource, only mirrors it verbatim.
        var eggGroupJson = Fixture("egg-group", "5");
        for (var eggGroupId = 1; eggGroupId <= 15; eggGroupId++)
        {
            responses[Upstream($"egg-group/{eggGroupId}/index.json")] = eggGroupJson;
        }

        return new StubHandler(responses, notFoundUrl);
    }

    /// <summary>A handler serving only the species-list URL — sufficient for
    /// the list-shape refusal tests, since a list the fetcher refuses is
    /// refused before any species/pokemon/chain/egg-group request is ever
    /// made.</summary>
    private static StubHandler HandlerServingList(string listJson) =>
        new(
            new Dictionary<string, string>(StringComparer.Ordinal) { [Upstream("pokemon-species/index.json")] = listJson },
            notFoundUrl: null);

    private static string Upstream(string path) => $"{BaseUrl}{Pin}/data/api/v2/{path}";

    private static string Fixture(string resource, string id) =>
        File.ReadAllText(Path.Combine(FixturesDirectory, resource, $"{id}.json"));

    private sealed class StubHandler(IReadOnlyDictionary<string, string> responses, string? notFoundUrl)
        : HttpMessageHandler
    {
        public readonly Dictionary<string, int> CallCounts = new(StringComparer.Ordinal);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            CallCounts[url] = CallCounts.TryGetValue(url, out var count) ? count + 1 : 1;

            if (url == notFoundUrl)
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            }

            return Task.FromResult(responses.TryGetValue(url, out var body)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
