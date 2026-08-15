using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using PokemonInvestBatch.Application.Pokedex;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.TestSupport;
using PokemonInvestBatch.Worker.Lanes;

namespace PokemonInvestBatch.Worker.Tests;

/// <summary>
/// <see cref="PokedexLane.RunSweepAsync"/> end to end against a real database
/// and pre-placed fixture mirrors: the PokéAPI dataset imports, every icon
/// resolves to Skipped, cards tag by title match against the imported
/// species, and set_details fills in against an (empty) TCGdex catalog — with
/// every count reconciling in one composite result, and not one network
/// request ever reaching the transport layer (see
/// <see cref="NetworkFreeHttpClientFactory"/>).
/// </summary>
public class PokedexLaneTests : DatabaseTest, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Task 6/7's flat fixture layout, copied into this project too:
    /// Umbreon(197) and its Eevee-line context(133) share evolution-chain 67;
    /// Type: Null(772) carries chain 399 alone.</summary>
    private static readonly string FixturesDirectory =
        Path.Combine(AppContext.BaseDirectory, "Pokedex", "Fixtures");

    private static readonly int[] FixtureDexNumbers = [133, 197, 772];

    private readonly string _pokeapiMirrorDirectory =
        Path.Combine(Path.GetTempPath(), $"pokeapi-mirror-{Guid.NewGuid():N}");

    private readonly string _tcgdexMirrorDirectory =
        Path.Combine(Path.GetTempPath(), $"tcgdex-mirror-{Guid.NewGuid():N}");

    private readonly string _iconDirectory =
        Path.Combine(Path.GetTempPath(), $"species-icons-{Guid.NewGuid():N}");

    public void Dispose()
    {
        foreach (var directory in new[] { _pokeapiMirrorDirectory, _tcgdexMirrorDirectory, _iconDirectory })
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>Copies the fixture species/pokemon/evolution-chain files into
    /// a throwaway directory and writes a manifest beside them, so
    /// <c>PokeapiMirror.Exists</c> sees a complete mirror and
    /// <c>PokeapiMirror.FetchAsync</c> never runs. Egg-group files are not
    /// copied — <c>PokeapiDataset.Load</c> reads egg groups off the species
    /// file itself via <c>PokedexMaps</c>, never off disk.</summary>
    private async Task WritePokeapiMirrorAsync()
    {
        foreach (var resource in new[] { "pokemon-species", "pokemon", "evolution-chain" })
        {
            var destination = Path.Combine(_pokeapiMirrorDirectory, resource);
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(Path.Combine(FixturesDirectory, resource), "*.json"))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            }
        }

        await File.WriteAllTextAsync(
            Path.Combine(_pokeapiMirrorDirectory, "pokeapi-mirror.manifest.json"),
            """{ "Pin": "test-pin", "FetchedAt": "2026-08-15T00:00:00+00:00", "FileCount": 8 }""");
    }

    /// <summary>An empty TCGdex mirror — zero sets, so every seeded CardSet
    /// can only ever resolve Unmapped/Pending, with no dependency on real
    /// TCGdex fixture content. <c>TcgdexMirror.Exists</c> sees a complete
    /// mirror from the manifest alone, so <c>TcgdexMirror.FetchAsync</c>
    /// never runs.</summary>
    private async Task WriteTcgdexMirrorAsync()
    {
        Directory.CreateDirectory(Path.Combine(_tcgdexMirrorDirectory, "sets"));
        await File.WriteAllTextAsync(
            Path.Combine(_tcgdexMirrorDirectory, "manifest.json"),
            """{ "FetchedAt": "2026-08-15T00:00:00+00:00", "ReleaseTag": "v-test", "SetCount": 0 }""");
    }

    /// <summary>Every fixture species' icon pre-placed, so
    /// <c>SpeciesIconStore.FetchMissingAsync</c>'s skip-if-exists gate fires
    /// for all three and issues zero requests — Task 8's idempotent skip is
    /// what makes a fully-warm icon step provably network-free.</summary>
    private async Task WriteIconsAsync()
    {
        Directory.CreateDirectory(_iconDirectory);
        foreach (var dex in FixtureDexNumbers)
        {
            await File.WriteAllBytesAsync(Path.Combine(_iconDirectory, $"{dex}.png"), [0x89, 0x50, 0x4E, 0x47]);
        }
    }

    private async Task SeedAsync()
    {
        await using var db = NewContext();
        db.Sets.Add(new CardSet
        {
            Id = 1,
            Slug = "pokemon-evolving-skies",
            Name = "Pokemon Evolving Skies",
            DiscoveredAt = Now,
            LastSeenAt = Now,
        });
        db.Cards.AddRange(
            // Title-matches Umbreon (197).
            new Card
            {
                Id = 101,
                SetId = 1,
                Url = "/game/pokemon-evolving-skies/umbreon-vmax-215",
                Name = "Umbreon VMAX #215",
                FirstSeenAt = Now,
                LastSeenAt = Now,
            },
            // A trainer card — matches no species in the fixture catalog.
            new Card
            {
                Id = 102,
                SetId = 1,
                Url = "/game/pokemon-evolving-skies/rare-candy-85",
                Name = "Rare Candy #85",
                FirstSeenAt = Now,
                LastSeenAt = Now,
            },
            // Not a card at all — must never be examined.
            new Card
            {
                Id = 103,
                SetId = 1,
                Url = "/game/pokemon-evolving-skies/game-boy",
                Name = "Game Boy",
                FirstSeenAt = Now,
                LastSeenAt = Now,
                NotACardAt = Now,
            },
            // Title-matches Type: Null (772) — proves the odd-punctuated
            // species name also composes correctly through the whole lane.
            new Card
            {
                Id = 104,
                SetId = 1,
                Url = "/game/pokemon-evolving-skies/type-null-1",
                Name = "Type: Null #1",
                FirstSeenAt = Now,
                LastSeenAt = Now,
            });
        await db.SaveChangesAsync();
    }

    private PokedexLane NewLane(ILogger<PokedexLane>? logger = null) => new(
        new OptionsContextFactory(ContextOptions()),
        new NetworkFreeHttpClientFactory(),
        TimeProvider.System,
        Options.Create(new ScraperOptions
        {
            PokedexMirrorDirectory = _pokeapiMirrorDirectory,
            SpeciesIconDirectory = _iconDirectory,
            TcgdexMirrorDirectory = _tcgdexMirrorDirectory,
            TcgdexSetAliasesPath = Path.Combine(_tcgdexMirrorDirectory, "no-aliases.json"),
            TcgdexSeriesEraPath = Path.Combine(_tcgdexMirrorDirectory, "no-eras.json"),
        }),
        logger ?? NullLogger<PokedexLane>.Instance);

    [SkippableFact]
    public async Task A_sweep_composes_every_stage_and_reports_the_receipt_counts()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await WritePokeapiMirrorAsync();
        await WriteTcgdexMirrorAsync();
        await WriteIconsAsync();
        await SeedAsync();

        var result = await NewLane().RunSweepAsync(CancellationToken.None);

        // Species: a fresh import of the three-species fixture.
        Assert.Equal(3, result.Species.Inserted);
        Assert.Equal(0, result.Species.Updated);
        Assert.Equal(0, result.Species.Unchanged);

        // Icons: every dex pre-placed, so every one is Skipped — zero requests.
        Assert.Equal(0, result.Icons.FromMenuIcons);
        Assert.Equal(0, result.Icons.FromDefaultSprites);
        Assert.Equal(3, result.Icons.Skipped);
        Assert.Equal(0, result.Icons.Missing);

        // Tagging: the not-a-card row is excluded from Examined; Umbreon VMAX
        // and Type: Null both title-match, Rare Candy matches no species.
        Assert.Equal(3, result.Tagging.Examined);
        Assert.Equal(2, result.Tagging.Tagged);
        Assert.Equal(1, result.Tagging.NoSpecies);
        Assert.Equal(0, result.Tagging.Quarantined);
        Assert.Equal(2, result.Tagging.LinksWritten);
        Assert.Equal(0, result.Tagging.LinksRemoved);

        // Set details: one set seeded, empty TCGdex catalog, so it is Pending.
        Assert.Equal(0, result.SetDetails.Matched);
        Assert.Equal(1, result.SetDetails.Pending);

        await using var db = NewContext();
        Assert.Equal(3, await db.SpeciesRows.CountAsync());

        var umbreonTagging = await db.CardTagging.SingleAsync(t => t.CardId == 101);
        Assert.Equal(TagStatus.Tagged, umbreonTagging.Status);
        Assert.Equal(TagMethod.TitleMatch, umbreonTagging.Method);
        var umbreonLink = await db.CardSpecies.SingleAsync(l => l.CardId == 101);
        Assert.Equal(197, umbreonLink.SpeciesId);

        Assert.Equal(TagStatus.NoSpecies, (await db.CardTagging.SingleAsync(t => t.CardId == 102)).Status);
        Assert.Equal(0, await db.CardSpecies.CountAsync(l => l.CardId == 102));

        // Never examined: the not-a-card row gets no tagging row at all.
        Assert.Equal(0, await db.CardTagging.CountAsync(t => t.CardId == 103));

        var typeNullTagging = await db.CardTagging.SingleAsync(t => t.CardId == 104);
        Assert.Equal(TagStatus.Tagged, typeNullTagging.Status);
        var typeNullLink = await db.CardSpecies.SingleAsync(l => l.CardId == 104);
        Assert.Equal(772, typeNullLink.SpeciesId);

        var setDetail = await db.SetDetails.SingleAsync(d => d.SetId == 1);
        Assert.Equal(SetMatchStatus.Pending, setDetail.MatchStatus);
        Assert.Null(setDetail.Code);
    }

    /// <summary>Spec §6's idempotency requirement, exercised across the whole
    /// composed lane at once rather than per sub-sweep: a second run over
    /// wholly unchanged inputs changes nothing, and — since every mirror and
    /// icon now unambiguously pre-exists on disk — still never touches the
    /// network.</summary>
    [SkippableFact]
    public async Task A_second_sweep_over_unchanged_inputs_examines_and_writes_nothing_new()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await WritePokeapiMirrorAsync();
        await WriteTcgdexMirrorAsync();
        await WriteIconsAsync();
        await SeedAsync();
        var lane = NewLane();
        await lane.RunSweepAsync(CancellationToken.None);

        var second = await lane.RunSweepAsync(CancellationToken.None);

        Assert.Equal(0, second.Species.Inserted);
        Assert.Equal(0, second.Species.Updated);
        Assert.Equal(3, second.Species.Unchanged);

        Assert.Equal(3, second.Icons.Skipped);
        Assert.Equal(0, second.Icons.FromMenuIcons);
        Assert.Equal(0, second.Icons.FromDefaultSprites);

        Assert.Equal(0, second.Tagging.Examined);
        Assert.Equal(0, second.Tagging.LinksWritten);
        Assert.Equal(0, second.Tagging.LinksRemoved);

        // SetDetailsSweep always reports current state, not a delta — still
        // one pending set, just written with no physical row change.
        Assert.Equal(0, second.SetDetails.Matched);
        Assert.Equal(1, second.SetDetails.Pending);
    }

    /// <summary>The phase's acceptance receipts (spec §7) are only real if
    /// they actually land in one log line — this pins that down directly
    /// rather than trusting the source reading correct, the same
    /// verify-don't-assume posture the rest of the suite takes toward
    /// computed values.</summary>
    [SkippableFact]
    public async Task The_sweep_logs_one_information_line_carrying_every_count()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await WritePokeapiMirrorAsync();
        await WriteTcgdexMirrorAsync();
        await WriteIconsAsync();
        await SeedAsync();
        var logger = new FakeLogger<PokedexLane>();

        await NewLane(logger).RunSweepAsync(CancellationToken.None);

        var record = Assert.Single(logger.Collector.GetSnapshot(), r => r.Level == LogLevel.Information);
        var message = record.Message;

        // Each value tied to its own label, not just "the number 3 appears
        // somewhere" — this is what would catch an argument silently landing
        // next to the wrong placeholder, not only a missing one.
        Assert.Contains("3 inserted", message, StringComparison.Ordinal);
        Assert.Contains("0 updated", message, StringComparison.Ordinal);
        Assert.Contains("0 unchanged", message, StringComparison.Ordinal);
        Assert.Contains("0 from-menu", message, StringComparison.Ordinal);
        Assert.Contains("0 from-default", message, StringComparison.Ordinal);
        Assert.Contains("3 skipped", message, StringComparison.Ordinal);
        Assert.Contains("0 missing", message, StringComparison.Ordinal);
        Assert.Contains("3 examined", message, StringComparison.Ordinal);
        Assert.Contains("2 tagged", message, StringComparison.Ordinal);
        Assert.Contains("1 no-species", message, StringComparison.Ordinal);
        Assert.Contains("0 quarantined", message, StringComparison.Ordinal);
        Assert.Contains("2 links written", message, StringComparison.Ordinal);
        Assert.Contains("0 links removed", message, StringComparison.Ordinal);
        Assert.Contains("0 matched", message, StringComparison.Ordinal);
        Assert.Contains("1 pending", message, StringComparison.Ordinal);

        // No stray "{Placeholder}" survives formatting — proves every named
        // hole in the template was actually filled, not silently mismatched
        // against the positional argument list.
        Assert.DoesNotContain('{', message);
        Assert.DoesNotContain('}', message);
    }

    private sealed class OptionsContextFactory(DbContextOptions<PokemonDbContext> options)
        : IDbContextFactory<PokemonDbContext>
    {
        public PokemonDbContext CreateDbContext() => new(options);
    }

    /// <summary>Proves the sweep never touches the network when every mirror
    /// and icon file is pre-placed. <c>CreateClient</c> itself must succeed —
    /// <c>SpeciesIconStore.FetchMissingAsync</c> always receives a
    /// constructed <see cref="HttpClient"/> even when every dex is skipped,
    /// so refusing to construct one (the <c>EnrichmentLaneTests</c> pattern)
    /// would fail this lane for a reason that has nothing to do with the
    /// network. The real guarantee lives one layer down: any actual request —
    /// <c>SendAsync</c> ever being invoked — throws immediately.</summary>
    private sealed class NetworkFreeHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ThrowingHandler());

        private sealed class ThrowingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new InvalidOperationException(
                    $"Unexpected network request to {request.RequestUri} — every mirror and icon file is " +
                    "pre-placed for this test, so the sweep must never actually send one.");
        }
    }
}
