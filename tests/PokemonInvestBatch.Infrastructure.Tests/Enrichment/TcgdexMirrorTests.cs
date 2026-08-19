using Microsoft.Extensions.Logging.Abstractions;
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
            http, "https://api.tcgdex.example", "en", _directory, TimeProvider.System, CancellationToken.None);

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
    public async Task The_fetch_uses_the_requested_locale()
    {
        var handler = new StubHandler(new Dictionary<string, string>
        {
            ["https://api.tcgdex.example/v2/ja/sets"] =
                """[ { "id": "SV2a", "name": "ポケモンカード151" } ]""",
            ["https://api.tcgdex.example/v2/ja/sets/SV2a"] = """
                {
                  "id": "SV2a",
                  "name": "ポケモンカード151",
                  "serie": { "id": "sv", "name": "ポケモンカードゲーム スカーレット&バイオレット" },
                  "releaseDate": "2023-06-16",
                  "cardCount": { "official": 165, "total": 210 },
                  "cards": [
                    { "id": "SV2a-025", "localId": "025", "name": "ピカチュウ" }
                  ]
                }
                """,
        });
        using var http = new HttpClient(handler);

        var manifest = await TcgdexMirror.FetchAsync(
            http, "https://api.tcgdex.example", "ja", _directory, TimeProvider.System, CancellationToken.None);

        // The locale is part of the pin: it decides the URLs fetched and is
        // recorded in the manifest so a directory says which shelf it holds.
        Assert.Equal("ja", manifest.Locale);
        var (catalog, loaded) = await TcgdexMirror.LoadAsync(_directory, CancellationToken.None);
        Assert.Equal("ja", loaded.Locale);
        Assert.Equal("ポケモンカード151", catalog.ById("SV2a")!.Name);
        Assert.Equal("ピカチュウ", Assert.Single(catalog.ById("SV2a")!.Cards).Name);
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
            http, "https://api.tcgdex.example", "en", _directory, TimeProvider.System, CancellationToken.None));
    }

    /// <summary>
    /// The race EnsureAsync exists to close: two lanes (EnrichmentLane and
    /// PokedexLane) can both find the mirror missing — a fresh box, or an
    /// operator deleting the directory to refresh — and both start fetching.
    /// This proves the second caller's fetch never happens at all, not just
    /// that its writes "happen to" land in a way that looks fine.
    ///
    /// Deterministic, not timing-based: <see cref="GatedCountingHandler"/>
    /// suspends on the very first HTTP request via an uncompleted
    /// <see cref="TaskCompletionSource"/>. Because <c>Exists</c> and an
    /// uncontended <c>SemaphoreSlim.WaitAsync</c> both complete synchronously,
    /// starting <c>first</c> runs synchronously all the way to that gated
    /// request — meaning by the time the assignment statement returns,
    /// <c>EnsureGate</c> is genuinely held. Starting <c>second</c> right after
    /// then genuinely blocks on the (contended) gate itself, not on any
    /// timing coincidence. Releasing the completion source afterward is what
    /// lets both finish, in the order the gate — not the test — decides.
    /// </summary>
    [Fact]
    public async Task EnsureAsync_lets_a_losing_concurrent_caller_skip_the_duplicate_fetch()
    {
        var releaseFirstRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new GatedCountingHandler(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://api.tcgdex.example/v2/en/sets"] =
                    """[ { "id": "swsh7", "name": "Evolving Skies" } ]""",
                ["https://api.tcgdex.example/v2/en/sets/swsh7"] = EvolvingSkiesJson,
                ["https://api.github.com/repos/tcgdex/cards-database/releases/latest"] =
                    """{ "tag_name": "v2.47.0" }""",
            },
            releaseFirstRequest);
        using var http = new HttpClient(handler);
        var clientRequests = 0;
        HttpClient NewClient()
        {
            clientRequests++;
            return http;
        }

        // Runs synchronously up to the gated request and suspends there —
        // EnsureGate is now held, and only the sets/ subdirectory (not the
        // manifest, written last) exists on disk.
        var first = TcgdexMirror.EnsureAsync(
            NewClient, "https://api.tcgdex.example", "en", _directory, TimeProvider.System, NullLogger.Instance,
            CancellationToken.None);
        Assert.False(TcgdexMirror.Exists(_directory));

        // Runs synchronously up to EnsureGate.WaitAsync, which is genuinely
        // contended (first holds it) and genuinely suspends — this call has
        // not skipped via its own pre-gate Exists() check (still false).
        var second = TcgdexMirror.EnsureAsync(
            NewClient, "https://api.tcgdex.example", "en", _directory, TimeProvider.System, NullLogger.Instance,
            CancellationToken.None);

        releaseFirstRequest.SetResult();
        await Task.WhenAll(first, second);

        Assert.True(TcgdexMirror.Exists(_directory));
        var (catalog, manifest) = await TcgdexMirror.LoadAsync(_directory, CancellationToken.None);
        Assert.Equal("v2.47.0", manifest.Version);
        Assert.NotNull(catalog.ById("swsh7"));

        // Exactly one fetch pass: every URL the fetch touches was requested
        // once, not twice — the loser made none of these requests itself.
        Assert.Equal(1, handler.CallCounts.GetValueOrDefault("https://api.tcgdex.example/v2/en/sets"));
        Assert.Equal(1, handler.CallCounts.GetValueOrDefault("https://api.tcgdex.example/v2/en/sets/swsh7"));
        Assert.Equal(
            1, handler.CallCounts.GetValueOrDefault("https://api.github.com/repos/tcgdex/cards-database/releases/latest"));
        // NewClient itself was only invoked once — the loser never even
        // built a client, let alone used one (the eager-construction bug
        // this signature shape exists to avoid; see EnsureAsync's doc).
        Assert.Equal(1, clientRequests);

        // A third, post-completion call — against the exact mirror the race
        // above just produced — is the steady state: exactly one list
        // request (the top-up freshness check), which finds nothing missing
        // and re-fetches no pinned document and rewrites no manifest.
        await TcgdexMirror.EnsureAsync(
            NewClient, "https://api.tcgdex.example", "en", _directory, TimeProvider.System, NullLogger.Instance,
            CancellationToken.None);
        Assert.Equal(2, handler.CallCounts.GetValueOrDefault("https://api.tcgdex.example/v2/en/sets"));
        Assert.Equal(1, handler.CallCounts.GetValueOrDefault("https://api.tcgdex.example/v2/en/sets/swsh7"));
        Assert.Equal(
            1, handler.CallCounts.GetValueOrDefault("https://api.github.com/repos/tcgdex/cards-database/releases/latest"));
        Assert.Equal(2, clientRequests);
    }

    private static TaskCompletionSource CompletedGate()
    {
        var gate = new TaskCompletionSource();
        gate.SetResult();
        return gate;
    }

    [Fact]
    public async Task A_topup_downloads_only_missing_sets_and_repins_the_manifest()
    {
        await WriteMirrorAsync(
            """{ "FetchedAt": "2026-08-13T00:00:00+00:00", "ReleaseTag": "v2.47.0", "SetCount": 1, "Locale": "en" }""",
            ("swsh7", EvolvingSkiesJson));
        var handler = new GatedCountingHandler(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://api.tcgdex.example/v2/en/sets"] =
                    """[ { "id": "swsh7", "name": "Evolving Skies" }, { "id": "A3b", "name": "Eevee Grove" } ]""",
                ["https://api.tcgdex.example/v2/en/sets/A3b"] = PocketJson,
                ["https://api.github.com/repos/tcgdex/cards-database/releases/latest"] =
                    """{ "tag_name": "v2.48.0" }""",
            },
            CompletedGate());
        using var http = new HttpClient(handler);

        await TcgdexMirror.EnsureAsync(
            () => http, "https://api.tcgdex.example", "en", _directory, TimeProvider.System,
            NullLogger.Instance, CancellationToken.None);

        // Only the set the mirror lacked was fetched; the pinned document
        // was never re-requested, and the manifest now covers both.
        Assert.Equal(1, handler.CallCounts.GetValueOrDefault("https://api.tcgdex.example/v2/en/sets"));
        Assert.Equal(0, handler.CallCounts.GetValueOrDefault("https://api.tcgdex.example/v2/en/sets/swsh7"));
        Assert.Equal(1, handler.CallCounts.GetValueOrDefault("https://api.tcgdex.example/v2/en/sets/A3b"));
        var (catalog, manifest) = await TcgdexMirror.LoadAsync(_directory, CancellationToken.None);
        Assert.Equal(2, manifest.SetCount);
        Assert.Equal("v2.48.0", manifest.ReleaseTag);
        Assert.NotNull(catalog.ById("swsh7"));
        Assert.NotNull(catalog.ById("A3b"));
    }

    [Fact]
    public async Task A_topup_with_nothing_missing_writes_nothing()
    {
        await WriteMirrorAsync(
            """{ "FetchedAt": "2026-08-13T00:00:00+00:00", "ReleaseTag": "v2.47.0", "SetCount": 1, "Locale": "en" }""",
            ("swsh7", EvolvingSkiesJson));
        var manifestPath = Path.Combine(_directory, "manifest.json");
        var manifestBefore = await File.ReadAllTextAsync(manifestPath);
        var handler = new GatedCountingHandler(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://api.tcgdex.example/v2/en/sets"] =
                    """[ { "id": "swsh7", "name": "Evolving Skies" } ]""",
            },
            CompletedGate());
        using var http = new HttpClient(handler);

        await TcgdexMirror.EnsureAsync(
            () => http, "https://api.tcgdex.example", "en", _directory, TimeProvider.System,
            NullLogger.Instance, CancellationToken.None);

        // The daily list check found the pin complete: no set fetched, no
        // manifest rewrite — the pin's fetch date still tells the truth.
        Assert.Equal(1, handler.CallCounts.GetValueOrDefault("https://api.tcgdex.example/v2/en/sets"));
        Assert.Equal(manifestBefore, await File.ReadAllTextAsync(manifestPath));
    }

    [Fact]
    public async Task An_interrupted_topup_heals_on_the_next_ensure()
    {
        // An interrupted top-up leaves more set files than the manifest
        // counts (the manifest is only rewritten at the end). Loading
        // tolerates the surplus, and the next ensure re-pins the manifest.
        await WriteMirrorAsync(
            """{ "FetchedAt": "2026-08-13T00:00:00+00:00", "SetCount": 1, "Locale": "en" }""",
            ("swsh7", EvolvingSkiesJson),
            ("A3b", PocketJson));

        var (catalog, manifest) = await TcgdexMirror.LoadAsync(_directory, CancellationToken.None);
        Assert.Equal(1, manifest.SetCount);
        Assert.NotNull(catalog.ById("A3b"));

        var handler = new GatedCountingHandler(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://api.tcgdex.example/v2/en/sets"] =
                    """[ { "id": "swsh7", "name": "Evolving Skies" }, { "id": "A3b", "name": "Eevee Grove" } ]""",
            },
            CompletedGate());
        using var http = new HttpClient(handler);
        await TcgdexMirror.EnsureAsync(
            () => http, "https://api.tcgdex.example", "en", _directory, TimeProvider.System,
            NullLogger.Instance, CancellationToken.None);

        Assert.Equal(0, handler.CallCounts.GetValueOrDefault("https://api.tcgdex.example/v2/en/sets/A3b"));
        var (_, healed) = await TcgdexMirror.LoadAsync(_directory, CancellationToken.None);
        Assert.Equal(2, healed.SetCount);
    }

    [Fact]
    public async Task An_interrupted_first_fetch_resumes_where_it_stopped()
    {
        // A first fetch that died mid-way (the 2026-08-19 ja fetch: one
        // stalled TLS read at document 103 of 177) leaves documents but no
        // manifest. The next attempt must not start over: already-landed
        // documents are skipped, only the remainder is fetched.
        Directory.CreateDirectory(Path.Combine(_directory, "sets"));
        await File.WriteAllTextAsync(Path.Combine(_directory, "sets", "swsh7.json"), EvolvingSkiesJson);

        var handler = new GatedCountingHandler(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["https://api.tcgdex.example/v2/en/sets"] =
                    """[ { "id": "swsh7", "name": "Evolving Skies" }, { "id": "A3b", "name": "Eevee Grove" } ]""",
                ["https://api.tcgdex.example/v2/en/sets/A3b"] = PocketJson,
            },
            CompletedGate());
        using var http = new HttpClient(handler);

        await TcgdexMirror.EnsureAsync(
            () => http, "https://api.tcgdex.example", "en", _directory, TimeProvider.System,
            NullLogger.Instance, CancellationToken.None);

        Assert.Equal(0, handler.CallCounts.GetValueOrDefault("https://api.tcgdex.example/v2/en/sets/swsh7"));
        Assert.Equal(1, handler.CallCounts.GetValueOrDefault("https://api.tcgdex.example/v2/en/sets/A3b"));
        var (catalog, manifest) = await TcgdexMirror.LoadAsync(_directory, CancellationToken.None);
        Assert.Equal(2, manifest.SetCount);
        Assert.NotNull(catalog.ById("swsh7"));
        Assert.NotNull(catalog.ById("A3b"));
    }

    [Fact]
    public async Task A_transient_document_failure_is_retried_once()
    {
        // One flaky response among ~180 must not abort a whole sweep for a
        // day; a second genuine failure still refuses loudly.
        var handler = new FlakyOnceHandler(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["https://api.tcgdex.example/v2/en/sets"] =
                """[ { "id": "swsh7", "name": "Evolving Skies" } ]""",
            ["https://api.tcgdex.example/v2/en/sets/swsh7"] = EvolvingSkiesJson,
        })
        { FailFirstRequestTo = "https://api.tcgdex.example/v2/en/sets/swsh7" };
        using var http = new HttpClient(handler);

        var manifest = await TcgdexMirror.FetchAsync(
            http, "https://api.tcgdex.example", "en", _directory, TimeProvider.System, CancellationToken.None);

        Assert.Equal(1, manifest.SetCount);
        Assert.Equal(2, handler.RequestsTo("https://api.tcgdex.example/v2/en/sets/swsh7"));
        var (catalog, _) = await TcgdexMirror.LoadAsync(_directory, CancellationToken.None);
        Assert.NotNull(catalog.ById("swsh7"));
    }

    /// <summary>Fails the first request to one URL with the transport-level
    /// exception a stalled read produces, then answers normally — the shape
    /// of the 2026-08-19 production failure.</summary>
    private sealed class FlakyOnceHandler(IReadOnlyDictionary<string, string> responses) : HttpMessageHandler
    {
        private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

        public required string FailFirstRequestTo { get; init; }

        public int RequestsTo(string url) => _counts.GetValueOrDefault(url);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            _counts[url] = _counts.GetValueOrDefault(url) + 1;
            if (url == FailFirstRequestTo && _counts[url] == 1)
            {
                throw new HttpRequestException("Simulated stalled transport read.");
            }

            return Task.FromResult(responses.TryGetValue(url, out var body)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task A_directory_of_one_locale_refuses_a_topup_as_another()
    {
        // A manifest without a Locale predates the locale-aware mirror and
        // is English by construction — so even it refuses a "ja" top-up.
        await WriteMirrorAsync(
            """{ "FetchedAt": "2026-08-13T00:00:00+00:00", "SetCount": 1 }""",
            ("swsh7", EvolvingSkiesJson));
        using var http = new HttpClient(new StubHandler(new Dictionary<string, string>()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => TcgdexMirror.EnsureAsync(
            () => http, "https://api.tcgdex.example", "ja", _directory, TimeProvider.System,
            NullLogger.Instance, CancellationToken.None));
    }

    /// <summary>Gates exactly the first <c>SendAsync</c> call on
    /// <paramref name="releaseFirstRequest"/>; every call after that —
    /// including a second concurrent caller's, once it stops being blocked at
    /// the semaphore — proceeds immediately. Tracks every URL's call count so
    /// a test can assert a fetch pass touched each URL exactly once.</summary>
    private sealed class GatedCountingHandler(
        IReadOnlyDictionary<string, string> responses, TaskCompletionSource releaseFirstRequest) : HttpMessageHandler
    {
        private readonly Lock _lock = new();
        private bool _gatedOnce;

        public readonly Dictionary<string, int> CallCounts = new(StringComparer.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            bool shouldGate;
            lock (_lock)
            {
                CallCounts[url] = CallCounts.GetValueOrDefault(url) + 1;
                shouldGate = !_gatedOnce;
                _gatedOnce = true;
            }

            if (shouldGate)
            {
                await releaseFirstRequest.Task;
            }

            return responses.TryGetValue(url, out var body)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        }
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
