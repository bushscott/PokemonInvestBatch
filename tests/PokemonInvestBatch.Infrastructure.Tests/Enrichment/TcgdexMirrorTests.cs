using PokemonInvestBatch.Application.Enrichment;
using PokemonInvestBatch.Infrastructure.Enrichment;

namespace PokemonInvestBatch.Infrastructure.Tests.Enrichment;

public class TcgdexMirrorTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"tcgdex-mirror-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private const string EvolvingSkiesJson = """
        {
          "id": "swsh7",
          "name": "Evolving Skies",
          "serie": { "id": "swsh", "name": "Sword & Shield" },
          "releaseDate": "2021-08-27",
          "cardCount": { "official": 203, "total": 237, "holo": 130 },
          "cards": [
            { "id": "swsh7-215", "localId": "215", "name": "Umbreon VMAX" }
          ]
        }
        """;

    private const string PocketJson = """
        {
          "id": "A3b",
          "name": "Eevee Grove",
          "serie": { "id": "tcgp", "name": "Pokémon TCG Pocket" },
          "releaseDate": "2025-06-26",
          "cardCount": { "official": 69, "total": 107 },
          "cards": []
        }
        """;

    private async Task WriteMirrorAsync(string manifest, params (string Id, string Json)[] sets)
    {
        Directory.CreateDirectory(Path.Combine(_directory, "sets"));
        await File.WriteAllTextAsync(Path.Combine(_directory, "manifest.json"), manifest);
        foreach (var (id, json) in sets)
        {
            await File.WriteAllTextAsync(Path.Combine(_directory, "sets", $"{id}.json"), json);
        }
    }

    [Fact]
    public void A_directory_without_a_manifest_is_no_mirror()
    {
        // The manifest is written last by the fetch, so an interrupted fetch
        // reads as absent and simply re-fetches.
        Assert.False(TcgdexMirror.Exists(_directory));
    }

    [Fact]
    public async Task Loads_the_catalog_and_the_pin()
    {
        await WriteMirrorAsync(
            """{ "FetchedAt": "2026-08-13T00:00:00+00:00", "ReleaseTag": "v2.47.0", "SetCount": 2 }""",
            ("swsh7", EvolvingSkiesJson),
            ("A3b", PocketJson));

        var (catalog, manifest) = await TcgdexMirror.LoadAsync(_directory, CancellationToken.None);

        Assert.Equal("v2.47.0", manifest.Version);
        var set = Assert.Single(catalog.PhysicalSets);
        Assert.Equal("swsh7", set.Id);
        Assert.Equal(203, set.OfficialCount);
        Assert.Equal("Umbreon VMAX", Assert.Single(set.Cards).Name);
        // The Pocket set is in the mirror but never a physical candidate.
        Assert.NotNull(catalog.ById("A3b"));
        Assert.False(catalog.ById("A3b")!.IsPhysical);
    }

    [Fact]
    public async Task Without_a_release_tag_the_fetch_date_is_the_pin()
    {
        await WriteMirrorAsync(
            """{ "FetchedAt": "2026-08-13T09:30:00+00:00", "SetCount": 1 }""",
            ("swsh7", EvolvingSkiesJson));

        var (_, manifest) = await TcgdexMirror.LoadAsync(_directory, CancellationToken.None);

        Assert.Equal("api-2026-08-13", manifest.Version);
    }

    [Fact]
    public async Task A_set_count_disagreeing_with_the_manifest_refuses()
    {
        await WriteMirrorAsync(
            """{ "FetchedAt": "2026-08-13T00:00:00+00:00", "SetCount": 2 }""",
            ("swsh7", EvolvingSkiesJson));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TcgdexMirror.LoadAsync(_directory, CancellationToken.None));
    }

    [Fact]
    public async Task A_set_without_an_official_count_refuses()
    {
        await WriteMirrorAsync(
            """{ "FetchedAt": "2026-08-13T00:00:00+00:00", "SetCount": 1 }""",
            ("broken", """
                {
                  "id": "broken",
                  "name": "Broken",
                  "serie": { "id": "swsh" },
                  "cardCount": { "total": 10 },
                  "cards": []
                }
                """));

        // The join computes from cardCount.official; a shape without it is
        // drift, and drift refuses rather than guesses.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TcgdexMirror.LoadAsync(_directory, CancellationToken.None));
    }

    [Fact]
    public async Task The_fetch_mirrors_every_set_and_pins_last()
    {
        var handler = new StubHandler(new Dictionary<string, string>
        {
            ["https://api.tcgdex.example/v2/en/sets"] =
                """[ { "id": "swsh7", "name": "Evolving Skies" }, { "id": "A3b", "name": "Eevee Grove" } ]""",
            ["https://api.tcgdex.example/v2/en/sets/swsh7"] = EvolvingSkiesJson,
            ["https://api.tcgdex.example/v2/en/sets/A3b"] = PocketJson,
            ["https://api.github.com/repos/tcgdex/cards-database/releases/latest"] =
                """{ "tag_name": "v2.47.0" }""",
        });
        using var http = new HttpClient(handler);

        var manifest = await TcgdexMirror.FetchAsync(
            http, "https://api.tcgdex.example", _directory, TimeProvider.System, CancellationToken.None);

        Assert.Equal(2, manifest.SetCount);
        Assert.Equal("v2.47.0", manifest.ReleaseTag);
        Assert.True(TcgdexMirror.Exists(_directory));
        var (catalog, loaded) = await TcgdexMirror.LoadAsync(_directory, CancellationToken.None);
        Assert.Equal("v2.47.0", loaded.Version);
        // Both sets are mirrored; only the physical one is a join candidate.
        Assert.NotNull(catalog.ById("swsh7"));
        Assert.NotNull(catalog.ById("A3b"));
        Assert.Single(catalog.PhysicalSets);
    }

    [Fact]
    public async Task A_set_id_that_is_not_a_safe_file_name_refuses_the_mirror()
    {
        var handler = new StubHandler(new Dictionary<string, string>
        {
            ["https://api.tcgdex.example/v2/en/sets"] = """[ { "id": "../escape" } ]""",
        });
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<InvalidOperationException>(() => TcgdexMirror.FetchAsync(
            http, "https://api.tcgdex.example", _directory, TimeProvider.System, CancellationToken.None));
    }

    private sealed class StubHandler(IReadOnlyDictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            return Task.FromResult(responses.TryGetValue(url, out var body)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
