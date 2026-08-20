using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PokemonInvestBatch.Application.Enrichment;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.TestSupport;
using PokemonInvestBatch.Worker.Lanes;

namespace PokemonInvestBatch.Worker.Tests;

/// <summary>
/// The enrichment sweep end to end against a real database and a fixture
/// mirror: verdicts land once, an unchanged re-run writes nothing, and a
/// changed input appends rather than edits (ADR-0009).
/// </summary>
public class EnrichmentLaneTests : DatabaseTest, IDisposable
{
    private readonly string _mirrorDirectory =
        Path.Combine(Path.GetTempPath(), $"tcgdex-mirror-{Guid.NewGuid():N}");

    private readonly string _jaMirrorDirectory =
        Path.Combine(Path.GetTempPath(), $"tcgdex-mirror-ja-{Guid.NewGuid():N}");

    public void Dispose()
    {
        foreach (var directory in new[] { _mirrorDirectory, _jaMirrorDirectory })
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>An empty ja mirror by default — with no aliases, every
    /// Japanese set stays UnmappedSet exactly as before the ja join.</summary>
    private async Task WriteFixtureJaMirrorAsync(bool withPokemon151 = false)
    {
        Directory.CreateDirectory(Path.Combine(_jaMirrorDirectory, "sets"));
        if (withPokemon151)
        {
            await File.WriteAllTextAsync(
                Path.Combine(_jaMirrorDirectory, "sets", "SV2a.json"),
                """
                {
                  "id": "SV2a",
                  "name": "ポケモンカード151",
                  "serie": { "id": "sv", "name": "ポケモンカードゲーム スカーレット&バイオレット" },
                  "releaseDate": "2023-06-16",
                  "cardCount": { "official": 165, "total": 210 },
                  "cards": [
                    { "id": "SV2a-025", "localId": "025", "name": "ピカチュウ" },
                    { "id": "SV2a-159", "localId": "159", "name": "ハイパーボール" }
                  ]
                }
                """);
        }

        await File.WriteAllTextAsync(
            Path.Combine(_jaMirrorDirectory, "manifest.json"),
            $$"""{ "FetchedAt": "2026-08-19T00:00:00+00:00", "ReleaseTag": "v-test-ja", "SetCount": {{(withPokemon151 ? 1 : 0)}}, "Locale": "ja" }""");
    }

    private async Task WriteFixtureMirrorAsync()
    {
        Directory.CreateDirectory(Path.Combine(_mirrorDirectory, "sets"));
        await File.WriteAllTextAsync(
            Path.Combine(_mirrorDirectory, "manifest.json"),
            """{ "FetchedAt": "2026-08-13T00:00:00+00:00", "ReleaseTag": "v-test", "SetCount": 2 }""");
        await File.WriteAllTextAsync(
            Path.Combine(_mirrorDirectory, "sets", "swsh7.json"),
            """
            {
              "id": "swsh7",
              "name": "Evolving Skies",
              "serie": { "id": "swsh", "name": "Sword & Shield" },
              "releaseDate": "2021-08-27",
              "cardCount": { "official": 203, "total": 237 },
              "cards": [
                { "id": "swsh7-95", "localId": "95", "name": "Umbreon VMAX" },
                { "id": "swsh7-215", "localId": "215", "name": "Umbreon VMAX" }
              ]
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(_mirrorDirectory, "sets", "cel25.json"),
            """
            {
              "id": "cel25",
              "name": "Celebrations",
              "serie": { "id": "swsh", "name": "Sword & Shield" },
              "releaseDate": "2021-10-08",
              "cardCount": { "official": 25, "total": 25 },
              "cards": [
                { "id": "cel25-4", "localId": "4", "name": "Palkia" }
              ]
            }
            """);
    }

    private async Task SeedAsync()
    {
        await using var db = NewContext();
        var now = DateTimeOffset.UtcNow;
        db.Sets.AddRange(
            new CardSet { Id = 1, Slug = "pokemon-evolving-skies", Name = "Pokemon Evolving Skies", DiscoveredAt = now, LastSeenAt = now },
            new CardSet { Id = 2, Slug = "pokemon-celebrations", Name = "Pokemon Celebrations", DiscoveredAt = now, LastSeenAt = now },
            new CardSet { Id = 3, Slug = "pokemon-japanese-eevee-heroes", Name = "Pokemon Japanese Eevee Heroes", DiscoveredAt = now, LastSeenAt = now });
        db.Cards.AddRange(
            new Card { Id = 101, SetId = 1, Url = "/game/pokemon-evolving-skies/umbreon-vmax-215", Name = "Umbreon VMAX #215", FirstSeenAt = now, LastSeenAt = now },
            new Card { Id = 102, SetId = 1, Url = "/game/pokemon-evolving-skies/booster-box", Name = "Booster Box [1st Edition]", FirstSeenAt = now, LastSeenAt = now },
            new Card { Id = 103, SetId = 3, Url = "/game/pokemon-japanese-eevee-heroes/eevee-1", Name = "Eevee #1", FirstSeenAt = now, LastSeenAt = now },
            new Card { Id = 104, SetId = 1, Url = "/game/pokemon-evolving-skies/game-boy", Name = "Game Boy", FirstSeenAt = now, LastSeenAt = now, NotACardAt = now },
            new Card { Id = 105, SetId = 2, Url = "/game/pokemon-celebrations/charizard-4", Name = "Charizard #4", FirstSeenAt = now, LastSeenAt = now });
        await db.SaveChangesAsync();
    }

    private EnrichmentLane NewLane() => new(
        new OptionsContextFactory(ContextOptions()),
        new UnusedHttpClientFactory(),
        TimeProvider.System,
        Options.Create(new ScraperOptions
        {
            TcgdexMirrorDirectory = _mirrorDirectory,
            TcgdexJaMirrorDirectory = _jaMirrorDirectory,
            TcgdexSetAliasesPath = Path.Combine(_mirrorDirectory, "no-aliases.json"),
            TcgdexJaSetAliasesPath = Path.Combine(_jaMirrorDirectory, "ja-aliases.json"),
        }),
        NullLogger<EnrichmentLane>.Instance);

    [SkippableFact]
    public async Task A_sweep_writes_one_honest_verdict_per_card()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await WriteFixtureMirrorAsync();
        await WriteFixtureJaMirrorAsync();
        await SeedAsync();

        var result = await NewLane().RunSweepAsync(CancellationToken.None);

        // Four cards processed — the not-a-card page has nothing to enrich.
        Assert.Equal(4, result.Cards);
        Assert.Equal(4, result.RowsWritten);
        Assert.Equal("v-test", result.Version);

        await using var db = NewContext();
        var rows = await db.TcgdexEnrichments.OrderBy(e => e.CardId).ToListAsync();
        Assert.Equal(4, rows.Count);

        var confirmed = rows.Single(r => r.CardId == 101);
        Assert.Equal(TcgdexMatchStatus.Confirmed, confirmed.Status);
        Assert.Equal("215", confirmed.CardNumber);
        Assert.Equal(203, confirmed.SetOfficialSize);
        Assert.Equal("swsh7-215", confirmed.TcgdexCardId);
        Assert.Equal("v-test", confirmed.TcgdexVersion);

        Assert.Equal(TcgdexMatchStatus.NoNumber, rows.Single(r => r.CardId == 102).Status);
        Assert.Equal(TcgdexMatchStatus.UnmappedSet, rows.Single(r => r.CardId == 103).Status);

        // The name gate at work: Celebrations #4 is Palkia, not Charizard.
        var mismatch = rows.Single(r => r.CardId == 105);
        Assert.Equal(TcgdexMatchStatus.NameMismatch, mismatch.Status);
        Assert.Null(mismatch.CardNumber);
        Assert.Equal("Palkia", mismatch.TcgdexName);
    }

    [SkippableFact]
    public async Task An_unchanged_re_run_writes_nothing()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await WriteFixtureMirrorAsync();
        await WriteFixtureJaMirrorAsync();
        await SeedAsync();
        var lane = NewLane();

        await lane.RunSweepAsync(CancellationToken.None);
        var second = await lane.RunSweepAsync(CancellationToken.None);

        Assert.Equal(0, second.RowsWritten);
        await using var db = NewContext();
        Assert.Equal(4, await db.TcgdexEnrichments.CountAsync());
    }

    [SkippableFact]
    public async Task A_changed_verdict_appends_and_the_trail_survives()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await WriteFixtureMirrorAsync();
        await WriteFixtureJaMirrorAsync();
        await SeedAsync();
        var lane = NewLane();
        await lane.RunSweepAsync(CancellationToken.None);

        await using (var db = NewContext())
        {
            // The site renamed the product to the other Umbreon VMAX.
            (await db.Cards.SingleAsync(c => c.Id == 101)).Name = "Umbreon VMAX #95";
            await db.SaveChangesAsync();
        }

        var result = await lane.RunSweepAsync(CancellationToken.None);

        Assert.Equal(1, result.RowsWritten);
        await using var check = NewContext();
        var trail = await check.TcgdexEnrichments
            .Where(e => e.CardId == 101)
            .OrderBy(e => e.ComputedAt)
            .ToListAsync();
        // Appended, never edited: both verdicts survive, latest wins.
        Assert.Equal(2, trail.Count);
        Assert.Equal("215", trail[0].CardNumber);
        Assert.Equal("95", trail[^1].CardNumber);
    }

    [SkippableFact]
    public async Task A_mapped_japanese_card_with_species_agreement_confirms_end_to_end()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await WriteFixtureMirrorAsync();
        await WriteFixtureJaMirrorAsync(withPokemon151: true);
        await File.WriteAllTextAsync(
            Path.Combine(_jaMirrorDirectory, "ja-aliases.json"),
            """[ { "slug": "pokemon-japanese-scarlet-&-violet-151", "tcgdex": ["SV2a"], "reason": "test" } ]""");
        await SeedAsync();
        await using (var seed = NewContext())
        {
            seed.Sets.Add(new CardSet
            {
                Id = 4,
                Slug = "pokemon-japanese-scarlet-&-violet-151",
                Name = "Pokemon Japanese Scarlet & Violet 151",
                DiscoveredAt = DateTimeOffset.UtcNow,
                LastSeenAt = DateTimeOffset.UtcNow,
            });
            seed.Cards.AddRange(
                new Card
                {
                    Id = 106,
                    SetId = 4,
                    Url = "/game/pokemon-japanese-scarlet-%26-violet-151/pikachu-025",
                    Name = "Pikachu #025",
                    FirstSeenAt = DateTimeOffset.UtcNow,
                    LastSeenAt = DateTimeOffset.UtcNow,
                },
                new Card
                {
                    Id = 107,
                    SetId = 4,
                    Url = "/game/pokemon-japanese-scarlet-%26-violet-151/ultra-ball-159",
                    Name = "Ultra Ball #159",
                    FirstSeenAt = DateTimeOffset.UtcNow,
                    LastSeenAt = DateTimeOffset.UtcNow,
                });
            seed.SpeciesRows.Add(new Species
            {
                Id = 25,
                Name = "Pikachu",
                Slug = "pikachu",
                Generation = 1,
                Region = "Kanto",
                Color = "yellow",
                GradientStart = "#F5DA26",
                GradientEnd = "#C5A812",
            });
            seed.SpeciesNames.Add(new SpeciesName { SpeciesId = 25, Language = "ja", Name = "ピカチュウ" });
            seed.CardSpecies.Add(new CardSpeciesLink { CardId = 106, SpeciesId = 25 });
            await seed.SaveChangesAsync();
        }

        await NewLane().RunSweepAsync(CancellationToken.None);

        await using var db = NewContext();
        var rows = await db.TcgdexEnrichments.ToListAsync();

        // The guarded ja join: number + species agreement → Confirmed, with
        // the ja mirror's own version as provenance.
        var confirmed = rows.Single(r => r.CardId == 106);
        Assert.Equal(TcgdexMatchStatus.Confirmed, confirmed.Status);
        Assert.Equal("025", confirmed.CardNumber);
        Assert.Equal(165, confirmed.SetOfficialSize);
        Assert.Equal("SV2a", confirmed.TcgdexSetId);
        Assert.Equal("SV2a-025", confirmed.TcgdexCardId);
        Assert.Equal("ピカチュウ", confirmed.TcgdexName);
        Assert.Equal("v-test-ja", confirmed.TcgdexVersion);

        // The trainer: number matched, but no species on either side — the
        // honest no-guard status, nothing written.
        var trainer = rows.Single(r => r.CardId == 107);
        Assert.Equal(TcgdexMatchStatus.NoSpeciesGuard, trainer.Status);
        Assert.Null(trainer.CardNumber);
        Assert.Null(trainer.TcgdexCardId);

        // The unaliased Japanese set is untouched by the ja join existing.
        Assert.Equal(TcgdexMatchStatus.UnmappedSet, rows.Single(r => r.CardId == 103).Status);
    }

    private sealed class OptionsContextFactory(DbContextOptions<PokemonDbContext> options)
        : IDbContextFactory<PokemonDbContext>
    {
        public PokemonDbContext CreateDbContext() => new(options);
    }

    /// <summary>Against a pre-placed mirror the sweep's one sanctioned
    /// request is the top-up freshness check. This answers that list with
    /// exactly the ids the fixture mirror already pins — nothing missing, so
    /// nothing is fetched and the manifest is untouched — and any other
    /// request throws, keeping these tests provably network-free beyond that
    /// single check.</summary>
    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new TopUpListOnlyHandler());

        private sealed class TopUpListOnlyHandler : HttpMessageHandler
        {
            /// <summary>Both locales' list checks; each answers with ids the
            /// fixture mirrors already pin (the ja list answers empty — the
            /// directional load guard tolerates on-disk surplus), so nothing
            /// is ever downloaded.</summary>
            private static readonly IReadOnlyDictionary<string, string> Sanctioned =
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["https://api.tcgdex.net/v2/en/sets"] = """[ { "id": "swsh7" }, { "id": "cel25" } ]""",
                    ["https://api.tcgdex.net/v2/ja/sets"] = "[]",
                };

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) =>
                Sanctioned.TryGetValue(request.RequestUri!.ToString(), out var body)
                    ? Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                    {
                        Content = new StringContent(body),
                    })
                    : throw new InvalidOperationException(
                        $"Unexpected network request to {request.RequestUri} — the sweep's only sanctioned " +
                        "requests against pre-placed mirrors are the top-up list checks.");
        }
    }
}
