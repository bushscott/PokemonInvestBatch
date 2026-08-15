using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using PokemonInvestBatch.Infrastructure.Pokedex;

namespace PokemonInvestBatch.Infrastructure.Tests.Pokedex;

public class SpeciesIconStoreTests : IDisposable
{
    private const string BaseUrl = "https://sprites.example/";
    private const string Pin = "test-pin-icons-0001";

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"species-icon-store-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_dex_served_at_the_gen_viii_menu_icon_path_is_written_from_there_and_counted()
    {
        var menuIconUrl = Upstream("versions/generation-viii/icons/197.png");
        var bytes = FakePng(197);
        var handler = new StubHandler(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [menuIconUrl] = bytes,
        });
        using var http = new HttpClient(handler);

        var result = await SpeciesIconStore.FetchMissingAsync(
            http, BaseUrl, Pin, _directory, [197], NullLogger.Instance, CancellationToken.None);

        Assert.Equal(1, result.FromMenuIcons);
        Assert.Equal(0, result.FromDefaultSprites);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.Missing);
        Assert.Empty(result.MissingDexNumbers);

        var written = Path.Combine(_directory, "197.png");
        Assert.True(File.Exists(written));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(written));

        // The fallback tier must never be asked once the menu icon 200'd.
        Assert.False(handler.CallCounts.ContainsKey(Upstream("197.png")));
        Assert.Equal(1, handler.CallCounts[menuIconUrl]);

        // The atomic-write temp file must not survive a successful write.
        AssertNoTmpFilesRemain();
    }

    [Fact]
    public async Task A_dex_missing_the_menu_icon_falls_back_to_the_default_sprite_and_is_counted()
    {
        var defaultUrl = Upstream("1002.png");
        var bytes = FakePng(1002);
        var handler = new StubHandler(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [defaultUrl] = bytes,
        });
        using var http = new HttpClient(handler);

        var result = await SpeciesIconStore.FetchMissingAsync(
            http, BaseUrl, Pin, _directory, [1002], NullLogger.Instance, CancellationToken.None);

        Assert.Equal(0, result.FromMenuIcons);
        Assert.Equal(1, result.FromDefaultSprites);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(0, result.Missing);

        var written = Path.Combine(_directory, "1002.png");
        Assert.True(File.Exists(written));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(written));

        // Both tiers were genuinely asked, menu icon first — not skipped past.
        Assert.Equal(1, handler.CallCounts[Upstream("versions/generation-viii/icons/1002.png")]);
        Assert.Equal(1, handler.CallCounts[defaultUrl]);

        AssertNoTmpFilesRemain();
    }

    [Fact]
    public async Task A_dex_missing_at_both_tiers_is_recorded_as_a_gap_with_no_file_and_no_throw()
    {
        using var http = new HttpClient(new StubHandler(new Dictionary<string, byte[]>(StringComparer.Ordinal)));

        var result = await SpeciesIconStore.FetchMissingAsync(
            http, BaseUrl, Pin, _directory, [9999], NullLogger.Instance, CancellationToken.None);

        Assert.Equal(0, result.FromMenuIcons);
        Assert.Equal(0, result.FromDefaultSprites);
        Assert.Equal(0, result.Skipped);
        Assert.Equal(1, result.Missing);
        Assert.Equal([9999], result.MissingDexNumbers);
        Assert.False(File.Exists(Path.Combine(_directory, "9999.png")));
        Assert.False(Directory.EnumerateFileSystemEntries(_directory).Any());
    }

    [Fact]
    public async Task The_missing_gap_is_logged_by_dex_number_at_warning_level()
    {
        using var http = new HttpClient(new StubHandler(new Dictionary<string, byte[]>(StringComparer.Ordinal)));
        var logger = new FakeLogger();

        await SpeciesIconStore.FetchMissingAsync(
            http, BaseUrl, Pin, _directory, [9999], logger, CancellationToken.None);

        var record = Assert.Single(logger.Collector.GetSnapshot());
        Assert.Equal(LogLevel.Warning, record.Level);
        Assert.Contains("9999", record.Message);
    }

    [Fact]
    public async Task A_pre_existing_icon_file_is_skipped_with_zero_requests_and_left_untouched()
    {
        Directory.CreateDirectory(_directory);
        var existing = FakePng(133);
        await File.WriteAllBytesAsync(Path.Combine(_directory, "133.png"), existing);

        // If the store wrongly re-fetched despite the file already existing,
        // this response would prove it by overwriting with different bytes.
        var handler = new StubHandler(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [Upstream("versions/generation-viii/icons/133.png")] = FakePng(255),
        });
        using var http = new HttpClient(handler);

        var result = await SpeciesIconStore.FetchMissingAsync(
            http, BaseUrl, Pin, _directory, [133], NullLogger.Instance, CancellationToken.None);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.FromMenuIcons);
        Assert.Equal(0, result.FromDefaultSprites);
        Assert.Equal(0, result.Missing);
        Assert.Empty(handler.CallCounts);

        Assert.Equal(existing, await File.ReadAllBytesAsync(Path.Combine(_directory, "133.png")));
    }

    [Fact]
    public async Task A_500_from_the_menu_icon_tier_throws_and_keeps_icons_already_written_this_run()
    {
        // 649 is an arbitrary second dex, chosen only to be visually
        // distinct from the 500 status code it triggers.
        var handler = new StubHandler(
            new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                [Upstream("versions/generation-viii/icons/197.png")] = FakePng(197),
            },
            failUrls: new HashSet<string>(StringComparer.Ordinal)
            {
                Upstream("versions/generation-viii/icons/649.png"),
            });
        using var http = new HttpClient(handler);

        // 197 is processed (and written) before 649 blows up the run —
        // list ordering of a small literal array is stable.
        await Assert.ThrowsAsync<HttpRequestException>(() => SpeciesIconStore.FetchMissingAsync(
            http, BaseUrl, Pin, _directory, [197, 649], NullLogger.Instance, CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(_directory, "197.png")));
        Assert.Equal(FakePng(197), await File.ReadAllBytesAsync(Path.Combine(_directory, "197.png")));
        Assert.False(File.Exists(Path.Combine(_directory, "649.png")));

        // 197's write finished and its temp file was renamed away; 649 never
        // got bytes to write at all (the 500 happens during the fetch,
        // before WriteAtomicallyAsync is ever called) — no orphan temp
        // either way.
        AssertNoTmpFilesRemain();
    }

    [Fact]
    public async Task A_500_from_the_default_sprite_tier_also_throws_rather_than_recording_a_gap()
    {
        // 1002 404s at the menu-icon tier (absent from the response map) and
        // then 500s at the default-sprite tier — the throw must still fire
        // even though the failure is the fallback tier, not the first one.
        var handler = new StubHandler(
            new Dictionary<string, byte[]>(StringComparer.Ordinal),
            failUrls: new HashSet<string>(StringComparer.Ordinal) { Upstream("1002.png") });
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => SpeciesIconStore.FetchMissingAsync(
            http, BaseUrl, Pin, _directory, [1002], NullLogger.Instance, CancellationToken.None));

        Assert.False(File.Exists(Path.Combine(_directory, "1002.png")));
    }

    [Fact]
    public async Task A_mixed_run_aggregates_skip_menu_fallback_and_missing_correctly()
    {
        // The shape every real sweep takes: one full dex list mixing every
        // outcome at once, exactly how PokedexLane calls this every run.
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(Path.Combine(_directory, "133.png"), FakePng(133));

        var handler = new StubHandler(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [Upstream("versions/generation-viii/icons/197.png")] = FakePng(197),
            [Upstream("1002.png")] = FakePng(1002),
        });
        using var http = new HttpClient(handler);

        var result = await SpeciesIconStore.FetchMissingAsync(
            http, BaseUrl, Pin, _directory, [133, 197, 1002, 9999], NullLogger.Instance, CancellationToken.None);

        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, result.FromMenuIcons);
        Assert.Equal(1, result.FromDefaultSprites);
        Assert.Equal(1, result.Missing);
        Assert.Equal([9999], result.MissingDexNumbers);

        AssertNoTmpFilesRemain();
    }

    [Fact]
    public async Task A_stray_tmp_file_left_by_an_earlier_crash_does_not_block_the_next_write()
    {
        // Stands in for a process kill mid-write on a prior run (systemd
        // restart, OOM on the Pi): a leftover temp file for this exact dex,
        // seeded with garbage bytes that must never reach the real path.
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(Path.Combine(_directory, "197.png.tmp"), [0x00, 0x01, 0x02]);

        var bytes = FakePng(197);
        var handler = new StubHandler(new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [Upstream("versions/generation-viii/icons/197.png")] = bytes,
        });
        using var http = new HttpClient(handler);

        var result = await SpeciesIconStore.FetchMissingAsync(
            http, BaseUrl, Pin, _directory, [197], NullLogger.Instance, CancellationToken.None);

        Assert.Equal(1, result.FromMenuIcons);
        var written = Path.Combine(_directory, "197.png");
        Assert.True(File.Exists(written));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(written));

        AssertNoTmpFilesRemain();
    }

    private void AssertNoTmpFilesRemain() => Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));

    private static string Upstream(string path) => $"{BaseUrl}{Pin}/sprites/pokemon/{path}";

    /// <summary>A tiny, deliberately-not-a-real-PNG byte array, distinct per
    /// seed so "written bytes equal the stub's bytes" is a meaningful
    /// assertion rather than one that would pass by coincidence. The store
    /// never parses image content, only moves bytes, so realism beyond
    /// distinctness buys nothing here.</summary>
    private static byte[] FakePng(int seed) =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, unchecked((byte)seed), unchecked((byte)(seed >> 8))];

    private sealed class StubHandler(
        IReadOnlyDictionary<string, byte[]> responses, IReadOnlySet<string>? failUrls = null) : HttpMessageHandler
    {
        public readonly Dictionary<string, int> CallCounts = new(StringComparer.Ordinal);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            CallCounts[url] = CallCounts.TryGetValue(url, out var count) ? count + 1 : 1;

            if (failUrls?.Contains(url) == true)
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
            }

            return Task.FromResult(responses.TryGetValue(url, out var bytes)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        }
    }
}
