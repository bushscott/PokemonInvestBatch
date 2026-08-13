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

    public void Dispose()
    {
        if (Directory.Exists(_mirrorDirectory))
        {
            Directory.Delete(_mirrorDirectory, recursive: true);
        }
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
              "serie": { "id": "swsh" },
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
              "serie": { "id": "swsh" },
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
            TcgdexSetAliasesPath = Path.Combine(_mirrorDirectory, "no-aliases.json"),
        }),
        NullLogger<EnrichmentLane>.Instance);

    [SkippableFact]
    public async Task A_sweep_writes_one_honest_verdict_per_card()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await WriteFixtureMirrorAsync();
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

    private sealed class OptionsContextFactory(DbContextOptions<PokemonDbContext> options)
        : IDbContextFactory<PokemonDbContext>
    {
        public PokemonDbContext CreateDbContext() => new(options);
    }

    /// <summary>The sweep only touches the network when the mirror is absent;
    /// these tests always provide one, so a client is never created.</summary>
    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("The sweep must not fetch when a mirror exists.");
    }
}
