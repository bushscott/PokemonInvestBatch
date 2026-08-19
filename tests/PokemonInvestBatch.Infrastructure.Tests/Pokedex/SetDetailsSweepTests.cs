using Microsoft.EntityFrameworkCore;
using Npgsql;
using PokemonInvestBatch.Application.Enrichment;
using PokemonInvestBatch.Application.Pokedex;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.Infrastructure.Pokedex;
using PokemonInvestBatch.TestSupport;

namespace PokemonInvestBatch.Infrastructure.Tests.Pokedex;

/// <summary>
/// <see cref="SetDetailsSweep.RunAsync"/> against real PostgreSQL. Each test
/// builds and drops its own database; see DatabaseTest. The catalog fixtures
/// below use serie names and release dates read live from api.tcgdex.net on
/// 2026-08-15 (Evolving Skies, and the XY trainer-kit half-decks), not
/// invented values.
/// </summary>
public class SetDetailsSweepTests : DatabaseTest, IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _eraFilePath =
        Path.Combine(Path.GetTempPath(), $"tcgdex-series-eras-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_eraFilePath))
        {
            File.Delete(_eraFilePath);
        }
    }

    private static readonly TcgdexSet EvolvingSkies = new()
    {
        Id = "swsh7",
        Name = "Evolving Skies",
        SerieId = "swsh",
        SerieName = "Sword & Shield",
        ReleaseDate = new DateOnly(2021, 8, 27),
        OfficialCount = 203,
        TotalCount = 237,
    };

    private static readonly TcgdexSet SylveonHalfDeck = new()
    {
        Id = "tk-xy-sy",
        Name = "XY trainer Kit (Sylveon)",
        SerieId = "tk",
        SerieName = "Trainer kits",
        ReleaseDate = new DateOnly(2014, 3, 12),
        OfficialCount = 0,
        TotalCount = 20,
    };

    private static readonly TcgdexSet NoivernHalfDeck = new()
    {
        Id = "tk-xy-n",
        Name = "XY trainer Kit (Noivern)",
        SerieId = "tk",
        SerieName = "Trainer kits",
        ReleaseDate = new DateOnly(2014, 3, 12),
        OfficialCount = 0,
        TotalCount = 20,
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoAliases =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

    /// <summary>An empty Japanese shelf: wired, holding nothing — every
    /// Japanese set stays honestly Unmapped, exactly the pre-ja behavior.</summary>
    private static readonly SetMapper.JapaneseShelf NoJapanese =
        new(new TcgdexCatalog([]), NoAliases);

    /// <summary>Read from the pinned ja mirror 2026-08-19 (SV2a), not
    /// invented — same live-values rule as the fixtures above.</summary>
    private static readonly TcgdexSet Pokemon151Ja = new()
    {
        Id = "SV2a",
        Name = "ポケモンカード151",
        SerieId = "sv",
        SerieName = "ポケモンカードゲーム スカーレット&バイオレット",
        ReleaseDate = new DateOnly(2023, 6, 16),
        OfficialCount = 165,
        TotalCount = 210,
    };

    private static SetMapper.JapaneseShelf Japanese151Shelf() => new(
        new TcgdexCatalog([Pokemon151Ja]),
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["pokemon-japanese-scarlet-&-violet-151"] = ["SV2a"],
        });

    private Task WriteEraFileAsync(string content) => File.WriteAllTextAsync(_eraFilePath, content);

    [SkippableFact]
    public async Task A_mapped_set_is_matched_with_code_date_series_and_era()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await WriteEraFileAsync("""{ "Sword & Shield": "SWSH" }""");
        await using (var seed = NewContext())
        {
            seed.Sets.Add(SeedSet(1, "pokemon-evolving-skies", "Pokemon Evolving Skies"));
            await seed.SaveChangesAsync();
        }

        var sweep = new SetDetailsSweep(new TcgdexCatalog([EvolvingSkies]), NoAliases, NoJapanese, _eraFilePath);

        await using var db = NewContext();
        var result = await sweep.RunAsync(db, CancellationToken.None);

        Assert.Equal(1, result.Matched);
        Assert.Equal(0, result.Pending);

        await using var verify = NewContext();
        var detail = await verify.SetDetails.SingleAsync(d => d.SetId == 1);
        Assert.Equal(SetMatchStatus.Matched, detail.MatchStatus);
        Assert.Equal("swsh7", detail.Code);
        Assert.Equal(new DateOnly(2021, 8, 27), detail.ReleasedOn);
        Assert.Equal("Sword & Shield", detail.Series);
        Assert.Equal("SWSH", detail.Era);
    }

    [SkippableFact]
    public async Task An_unmapped_japanese_set_is_pending()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await using (var seed = NewContext())
        {
            seed.Sets.Add(SeedSet(3, "pokemon-japanese-eevee-heroes", "Pokemon Japanese Eevee Heroes"));
            await seed.SaveChangesAsync();
        }

        // Never written: File.Exists(_eraFilePath) is false, same as the
        // absent-file case this sweep must tolerate.
        var sweep = new SetDetailsSweep(new TcgdexCatalog([EvolvingSkies]), NoAliases, NoJapanese, _eraFilePath);

        await using var db = NewContext();
        var result = await sweep.RunAsync(db, CancellationToken.None);

        Assert.Equal(0, result.Matched);
        Assert.Equal(1, result.Pending);

        await using var verify = NewContext();
        var detail = await verify.SetDetails.SingleAsync(d => d.SetId == 3);
        Assert.Equal(SetMatchStatus.Pending, detail.MatchStatus);
        Assert.Null(detail.Code);
        Assert.Null(detail.ReleasedOn);
        Assert.Null(detail.Series);
        Assert.Null(detail.Era);
    }

    [SkippableFact]
    public async Task The_promo_grab_bag_set_is_pending_not_matched_to_one_era_promo_set()
    {
        // Kind=PromoPool never names exactly one TCGdex set — it fans out
        // per-card by number prefix (ADR-0009 Phase B), so set_details has
        // nothing honest to record at the set level.
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await using (var seed = NewContext())
        {
            seed.Sets.Add(SeedSet(4, SetMapper.PromoSlug, "Pokemon Promo"));
            await seed.SaveChangesAsync();
        }

        var sweep = new SetDetailsSweep(new TcgdexCatalog([EvolvingSkies]), NoAliases, NoJapanese, _eraFilePath);

        await using var db = NewContext();
        var result = await sweep.RunAsync(db, CancellationToken.None);

        Assert.Equal(0, result.Matched);
        Assert.Equal(1, result.Pending);

        await using var verify = NewContext();
        var detail = await verify.SetDetails.SingleAsync(d => d.SetId == 4);
        Assert.Equal(SetMatchStatus.Pending, detail.MatchStatus);
    }

    [SkippableFact]
    public async Task Without_an_era_file_era_is_null_for_every_matched_set()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await using (var seed = NewContext())
        {
            seed.Sets.Add(SeedSet(1, "pokemon-evolving-skies", "Pokemon Evolving Skies"));
            await seed.SaveChangesAsync();
        }

        var sweep = new SetDetailsSweep(new TcgdexCatalog([EvolvingSkies]), NoAliases, NoJapanese, _eraFilePath);

        await using var db = NewContext();
        await sweep.RunAsync(db, CancellationToken.None);

        await using var verify = NewContext();
        var detail = await verify.SetDetails.SingleAsync(d => d.SetId == 1);
        Assert.Equal(SetMatchStatus.Matched, detail.MatchStatus);
        Assert.Equal("swsh7", detail.Code); // still matched — code/date/series do not depend on the era file
        Assert.Equal("Sword & Shield", detail.Series);
        Assert.Null(detail.Era);
    }

    [SkippableFact]
    public async Task A_malformed_era_file_refuses_loudly_and_writes_nothing()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        // Valid JSON, invalid shape: an empty era name is the same "refuses
        // loudly" posture tcgdex-set-aliases.json's empty-target check uses.
        await WriteEraFileAsync("""{ "Sword & Shield": "" }""");
        await using (var seed = NewContext())
        {
            seed.Sets.Add(SeedSet(1, "pokemon-evolving-skies", "Pokemon Evolving Skies"));
            await seed.SaveChangesAsync();
        }

        var sweep = new SetDetailsSweep(new TcgdexCatalog([EvolvingSkies]), NoAliases, NoJapanese, _eraFilePath);

        await using var db = NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => sweep.RunAsync(db, CancellationToken.None));

        // The era file is read before any set_details row is touched, so a
        // refusal leaves no partial sweep behind.
        await using var verify = NewContext();
        Assert.Equal(0, await verify.SetDetails.CountAsync());
    }

    [SkippableFact]
    public async Task A_trainer_kit_alias_records_the_first_half_decks_details()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await using (var seed = NewContext())
        {
            seed.Sets.Add(SeedSet(5, "pokemon-sylveon-&-noivern", "Pokemon Sylveon & Noivern"));
            await seed.SaveChangesAsync();
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> aliases =
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["pokemon-sylveon-&-noivern"] = new[] { "tk-xy-sy", "tk-xy-n" },
            };
        var sweep = new SetDetailsSweep(new TcgdexCatalog([SylveonHalfDeck, NoivernHalfDeck]), aliases, NoJapanese, _eraFilePath);

        await using var db = NewContext();
        var result = await sweep.RunAsync(db, CancellationToken.None);

        Assert.Equal(1, result.Matched);

        await using var verify = NewContext();
        var detail = await verify.SetDetails.SingleAsync(d => d.SetId == 5);
        Assert.Equal(SetMatchStatus.Matched, detail.MatchStatus);
        Assert.Equal("tk-xy-sy", detail.Code); // the first alias target, by convention
        Assert.Equal(new DateOnly(2014, 3, 12), detail.ReleasedOn);
        Assert.Equal("Trainer kits", detail.Series);
    }

    [SkippableFact]
    public async Task A_rerun_over_unchanged_inputs_writes_nothing()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        await WriteEraFileAsync("""{ "Sword & Shield": "SWSH" }""");
        await using (var seed = NewContext())
        {
            seed.Sets.Add(SeedSet(1, "pokemon-evolving-skies", "Pokemon Evolving Skies"));
            await seed.SaveChangesAsync();
        }

        var sweep = new SetDetailsSweep(new TcgdexCatalog([EvolvingSkies]), NoAliases, NoJapanese, _eraFilePath);

        await using (var first = NewContext())
        {
            var firstResult = await sweep.RunAsync(first, CancellationToken.None);
            Assert.Equal(1, firstResult.Matched);
        }

        var xminBefore = await XminAsync("SELECT xmin::text FROM set_details WHERE set_id = 1");

        await using (var second = NewContext())
        {
            var result = await sweep.RunAsync(second, CancellationToken.None);
            Assert.Equal(1, result.Matched); // still reports the current state...
        }

        // ...but Postgres proves the physical row was never rewritten.
        Assert.Equal(xminBefore, await XminAsync("SELECT xmin::text FROM set_details WHERE set_id = 1"));
    }

    private async Task<string> XminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    [SkippableFact]
    public async Task A_mapped_japanese_set_writes_code_date_series_and_era()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        // The era file carries the Japanese serie name ordinal-exact, mapped
        // to the same era code the English shelf uses — that is what merges
        // the shelves downstream.
        await WriteEraFileAsync(
            """{ "Sword & Shield": "SWSH", "ポケモンカードゲーム スカーレット&バイオレット": "SV" }""");
        await using (var seed = NewContext())
        {
            seed.Sets.Add(SeedSet(5, "pokemon-japanese-scarlet-&-violet-151", "Pokemon Japanese Scarlet & Violet 151"));
            await seed.SaveChangesAsync();
        }

        var sweep = new SetDetailsSweep(
            new TcgdexCatalog([EvolvingSkies]), NoAliases, Japanese151Shelf(), _eraFilePath);

        await using var db = NewContext();
        var result = await sweep.RunAsync(db, CancellationToken.None);

        Assert.Equal(1, result.Matched);
        Assert.Equal(0, result.Pending);
        Assert.Equal((1, 0), result.Partitions[SetPartition.Japanese]);

        await using var verify = NewContext();
        var detail = await verify.SetDetails.SingleAsync(d => d.SetId == 5);
        Assert.Equal(SetMatchStatus.Matched, detail.MatchStatus);
        Assert.Equal("SV2a", detail.Code);
        Assert.Equal(new DateOnly(2023, 6, 16), detail.ReleasedOn);
        Assert.Equal("ポケモンカードゲーム スカーレット&バイオレット", detail.Series);
        Assert.Equal("SV", detail.Era);
    }

    [SkippableFact]
    public async Task A_japanese_serie_missing_from_the_era_file_stays_matched_with_null_era()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");
        // Only the English key present: the ja serie misses ordinal-exactly,
        // and a missing key means a null era on a still-Matched row — the
        // existing contract, unchanged by the ja join.
        await WriteEraFileAsync("""{ "Sword & Shield": "SWSH" }""");
        await using (var seed = NewContext())
        {
            seed.Sets.Add(SeedSet(6, "pokemon-japanese-scarlet-&-violet-151", "Pokemon Japanese Scarlet & Violet 151"));
            await seed.SaveChangesAsync();
        }

        var sweep = new SetDetailsSweep(
            new TcgdexCatalog([EvolvingSkies]), NoAliases, Japanese151Shelf(), _eraFilePath);

        await using var db = NewContext();
        await sweep.RunAsync(db, CancellationToken.None);

        await using var verify = NewContext();
        var detail = await verify.SetDetails.SingleAsync(d => d.SetId == 6);
        Assert.Equal(SetMatchStatus.Matched, detail.MatchStatus);
        Assert.Equal("SV2a", detail.Code);
        Assert.Null(detail.Era);
    }

    private static CardSet SeedSet(long id, string slug, string name) => new()
    {
        Id = id,
        Slug = slug,
        Name = name,
        DiscoveredAt = Now,
        LastSeenAt = Now,
    };
}
