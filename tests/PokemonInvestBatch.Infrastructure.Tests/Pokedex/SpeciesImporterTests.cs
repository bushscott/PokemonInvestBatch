using Microsoft.EntityFrameworkCore;
using Npgsql;
using PokemonInvestBatch.Application.Pokedex;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.Infrastructure.Pokedex;
using PokemonInvestBatch.TestSupport;

namespace PokemonInvestBatch.Infrastructure.Tests.Pokedex;

/// <summary>
/// <see cref="SpeciesImporter.ImportAsync"/> against real PostgreSQL. Each
/// test builds and drops its own database; see DatabaseTest. Fixture data
/// below is hand-built to exercise the importer's contract — it is not
/// sourced from the live PokéAPI dataset (that is Task 6's concern, already
/// covered by PokeapiDatasetTests).
/// </summary>
public class SpeciesImporterTests : DatabaseTest
{
    [SkippableFact]
    public async Task Importing_new_species_inserts_them_with_every_child_row()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        await using (var db = NewContext())
        {
            var result = await SpeciesImporter.ImportAsync(db, [Bulbasaur(), Ivysaur()], CancellationToken.None);

            Assert.Equal(2, result.Inserted);
            Assert.Equal(0, result.Updated);
            Assert.Equal(0, result.Unchanged);
            Assert.Equal(2, result.Inserted + result.Updated + result.Unchanged);
        }

        // A fresh context, so this reads what Postgres actually stored
        // rather than echoing the writer's own change tracker.
        await using var verify = NewContext();

        var bulbasaur = await verify.SpeciesRows.SingleAsync(s => s.Id == 1);
        Assert.Equal("Bulbasaur", bulbasaur.Name);
        Assert.Equal("bulbasaur", bulbasaur.Slug);
        Assert.Equal((short)1, bulbasaur.Generation);
        Assert.Equal("Kanto", bulbasaur.Region);
        Assert.Equal("Green", bulbasaur.Color);
        Assert.Equal("Grassland", bulbasaur.Habitat);
        Assert.Equal(SpeciesStatus.Ordinary, bulbasaur.Status);
        Assert.Equal((short)0, bulbasaur.Stage);
        Assert.Null(bulbasaur.EvolvesFromSpeciesId);
        Assert.Equal("#78C850", bulbasaur.GradientStart);
        Assert.Equal("#A7DB8D", bulbasaur.GradientEnd);

        var types = await verify.SpeciesTypes.Where(t => t.SpeciesId == 1).OrderBy(t => t.Slot).ToListAsync();
        Assert.Equal(new[] { "Grass", "Poison" }, types.Select(t => t.Type));
        Assert.Equal(new[] { (short)1, (short)2 }, types.Select(t => t.Slot));

        var eggGroups = await verify.SpeciesEggGroups
            .Where(e => e.SpeciesId == 1).Select(e => e.EggGroup).OrderBy(g => g).ToListAsync();
        Assert.Equal(new[] { "Grass", "Monster" }, eggGroups);

        var names = await verify.SpeciesNames.Where(n => n.SpeciesId == 1).ToDictionaryAsync(n => n.Language, n => n.Name);
        Assert.Equal(new Dictionary<string, string> { ["en"] = "Bulbasaur", ["ja"] = "フシギダネ" }, names);

        var ivysaur = await verify.SpeciesRows.SingleAsync(s => s.Id == 2);
        Assert.Equal("Ivysaur", ivysaur.Name);
        Assert.Equal((short)1, ivysaur.Stage);
        Assert.Equal(1, ivysaur.EvolvesFromSpeciesId);
    }

    [SkippableFact]
    public async Task Reimporting_identical_species_reports_unchanged_and_writes_nothing()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        await using (var seed = NewContext())
        {
            var seeded = await SpeciesImporter.ImportAsync(seed, [Bulbasaur(), Ivysaur()], CancellationToken.None);
            Assert.Equal(2, seeded.Inserted);
        }

        var speciesXminBefore = await XminAsync("SELECT xmin::text FROM species WHERE id = 1");
        var typeXminBefore = await XminAsync("SELECT xmin::text FROM species_types WHERE species_id = 1 AND slot = 1");
        var eggGroupXminBefore =
            await XminAsync("SELECT xmin::text FROM species_egg_groups WHERE species_id = 1 AND egg_group = 'Grass'");
        var nameXminBefore = await XminAsync("SELECT xmin::text FROM species_names WHERE species_id = 1 AND language = 'en'");

        await using (var db = NewContext())
        {
            var result = await SpeciesImporter.ImportAsync(db, [Bulbasaur(), Ivysaur()], CancellationToken.None);

            Assert.Equal(0, result.Inserted);
            Assert.Equal(0, result.Updated);
            Assert.Equal(2, result.Unchanged);
            Assert.Equal(2, result.Inserted + result.Updated + result.Unchanged);

            // Change-tracker proof, from inside the same call: the species
            // rows were loaded (tracked) to compare against the dataset,
            // but an unchanged species is never assigned to, so both stay
            // Unchanged; its child rows are loaded read-only (AsNoTracking)
            // and never RemoveRange'd/AddRange'd, so neither table's type
            // shows up in the tracker at all.
            Assert.All(db.ChangeTracker.Entries(), e => Assert.Equal(EntityState.Unchanged, e.State));
            Assert.DoesNotContain(
                db.ChangeTracker.Entries(),
                e => e.Entity is SpeciesType or SpeciesEggGroup or SpeciesName);
        }

        // Direct-from-Postgres proof: xmin advances on every UPDATE and on
        // a DELETE+INSERT replace, so identical values here mean the
        // physical rows were never touched — not merely that the ORM's
        // change tracker thought so.
        Assert.Equal(speciesXminBefore, await XminAsync("SELECT xmin::text FROM species WHERE id = 1"));
        Assert.Equal(typeXminBefore, await XminAsync("SELECT xmin::text FROM species_types WHERE species_id = 1 AND slot = 1"));
        Assert.Equal(
            eggGroupXminBefore,
            await XminAsync("SELECT xmin::text FROM species_egg_groups WHERE species_id = 1 AND egg_group = 'Grass'"));
        Assert.Equal(nameXminBefore, await XminAsync("SELECT xmin::text FROM species_names WHERE species_id = 1 AND language = 'en'"));
    }

    [SkippableFact]
    public async Task A_mixed_batch_reconciles_counts_and_only_writes_the_species_that_actually_changed()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        await using (var seed = NewContext())
        {
            await SpeciesImporter.ImportAsync(seed, [Bulbasaur(), Ivysaur()], CancellationToken.None);
        }

        var ivysaurXminBefore = await XminAsync("SELECT xmin::text FROM species WHERE id = 2");

        // Bulbasaur's name AND egg groups change together, so "replaced,
        // not duplicated" has something to catch below: an importer that
        // appends instead of replacing would leave 3 or 4 egg-group rows,
        // or leave the stale "Grass" row sitting beside the new "Dragon" one.
        var mutatedBulbasaur = Bulbasaur() with
        {
            Name = "Bulbasaur (renamed)",
            EggGroups = new[] { "Monster", "Dragon" },
        };

        await using (var db = NewContext())
        {
            var result = await SpeciesImporter.ImportAsync(
                db, [mutatedBulbasaur, Ivysaur(), Squirtle()], CancellationToken.None);

            Assert.Equal(1, result.Inserted); // Squirtle
            Assert.Equal(1, result.Updated); // Bulbasaur
            Assert.Equal(1, result.Unchanged); // Ivysaur
            Assert.Equal(3, result.Inserted + result.Updated + result.Unchanged);
        }

        await using var verify = NewContext();

        var bulbasaur = await verify.SpeciesRows.SingleAsync(s => s.Id == 1);
        Assert.Equal("Bulbasaur (renamed)", bulbasaur.Name);
        // Every other scalar is re-authored from the (otherwise identical) import record.
        Assert.Equal("bulbasaur", bulbasaur.Slug);

        var eggGroups = await verify.SpeciesEggGroups
            .Where(e => e.SpeciesId == 1).Select(e => e.EggGroup).OrderBy(g => g).ToListAsync();
        Assert.Equal(new[] { "Dragon", "Monster" }, eggGroups); // "Grass" is gone — replaced, not appended to.

        var types = await verify.SpeciesTypes.Where(t => t.SpeciesId == 1).ToListAsync();
        Assert.Equal(2, types.Count); // Untouched field: still exactly 2 rows, not duplicated by the replace.

        var squirtle = await verify.SpeciesRows.SingleAsync(s => s.Id == 7);
        Assert.Equal("Squirtle", squirtle.Name);
        var squirtleTypes = await verify.SpeciesTypes.Where(t => t.SpeciesId == 7).Select(t => t.Type).ToListAsync();
        Assert.Equal(new[] { "Water" }, squirtleTypes);

        Assert.Equal(ivysaurXminBefore, await XminAsync("SELECT xmin::text FROM species WHERE id = 2"));
    }

    [SkippableFact]
    public async Task Every_scalar_field_and_child_collection_change_is_detected_as_an_update()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        await using (var db = NewContext())
        {
            // A second, never-mutated row exists only so EvolvesFrom has a
            // legal target to point at partway through the sequence below —
            // the self-FK is enforced (Restrict), so a dangling reference
            // would throw before the comparison logic was ever exercised.
            db.SpeciesRows.Add(new Species
            {
                Id = 2,
                Name = "Anchor",
                Slug = "anchor",
                Generation = 1,
                Region = "Kanto",
                Color = "Green",
                Habitat = "Grassland",
                Status = SpeciesStatus.Ordinary,
                Stage = 0,
                EvolvesFromSpeciesId = null,
                GradientStart = "#000000",
                GradientEnd = "#111111",
            });
            await db.SaveChangesAsync();

            var seeded = await SpeciesImporter.ImportAsync(db, [Bulbasaur()], CancellationToken.None);
            Assert.Equal(1, seeded.Inserted);
        }

        // Each entry changes exactly one field relative to the previous
        // step's stored state — so a comparison that silently skips any one
        // of these would report Unchanged instead of Updated right here.
        (string Field, Func<SpeciesImport, SpeciesImport> Mutate)[] steps =
        [
            ("Name", s => s with { Name = "Bulbasaur Renamed" }),
            ("Slug", s => s with { Slug = "bulbasaur-alt" }),
            ("Generation", s => s with { Generation = 2 }),
            ("Region", s => s with { Region = "Johto" }),
            ("Color", s => s with { Color = "Blue" }),
            ("Habitat", s => s with { Habitat = "Cave" }),
            ("Status", s => s with { Status = SpeciesStatus.Legendary }),
            ("Stage", s => s with { Stage = 1 }),
            ("EvolvesFrom", s => s with { EvolvesFrom = 2 }),
            ("GradientStart", s => s with { GradientStart = "#123456" }),
            ("GradientEnd", s => s with { GradientEnd = "#654321" }),
            // Same two types, reversed slot order — proves the comparison
            // is slot-sensitive, not just a set-membership check.
            ("Types", s => s with { Types = new[] { "Poison", "Grass" } }),
            ("EggGroups", s => s with { EggGroups = new[] { "Monster", "Dragon" } }),
            ("LocalizedNames", s => s with
            {
                LocalizedNames = new Dictionary<string, string>(s.LocalizedNames) { ["ja"] = "Different" },
            }),
        ];

        var current = Bulbasaur();
        foreach (var (field, mutate) in steps)
        {
            var next = mutate(current);
            await using var db = NewContext();
            var result = await SpeciesImporter.ImportAsync(db, [next], CancellationToken.None);

            Assert.True(
                result is { Inserted: 0, Updated: 1, Unchanged: 0 },
                $"Changing only '{field}' should register as an update " +
                $"(got Inserted={result.Inserted}, Updated={result.Updated}, Unchanged={result.Unchanged}).");

            current = next;
        }

        await using var verify = NewContext();
        var stored = await verify.SpeciesRows.SingleAsync(s => s.Id == 1);
        Assert.Equal(current.Name, stored.Name);
        Assert.Equal(current.Slug, stored.Slug);
        Assert.Equal(current.Generation, stored.Generation);
        Assert.Equal(current.Region, stored.Region);
        Assert.Equal(current.Color, stored.Color);
        Assert.Equal(current.Habitat, stored.Habitat);
        Assert.Equal(current.Status, stored.Status);
        Assert.Equal(current.Stage, stored.Stage);
        Assert.Equal(current.EvolvesFrom, stored.EvolvesFromSpeciesId);
        Assert.Equal(current.GradientStart, stored.GradientStart);
        Assert.Equal(current.GradientEnd, stored.GradientEnd);

        var types = await verify.SpeciesTypes.Where(t => t.SpeciesId == 1).OrderBy(t => t.Slot).Select(t => t.Type).ToListAsync();
        Assert.Equal(current.Types, types);

        var eggGroups = await verify.SpeciesEggGroups.Where(e => e.SpeciesId == 1).Select(e => e.EggGroup).ToListAsync();
        Assert.Equal(current.EggGroups.OrderBy(g => g, StringComparer.Ordinal), eggGroups.OrderBy(g => g, StringComparer.Ordinal));

        var names = await verify.SpeciesNames.Where(n => n.SpeciesId == 1).ToDictionaryAsync(n => n.Language, n => n.Name);
        Assert.Equal(current.LocalizedNames, names);
    }

    [SkippableFact]
    public async Task A_species_that_evolves_from_a_higher_dex_ancestor_imports_successfully_in_dex_order()
    {
        Skip.If(!Available, "POKEMON_TEST_DB not set (needs a reachable PostgreSQL).");

        // PokeapiDataset.Load sorts results by Id ascending, and dex order
        // is not parent-first: Pikachu (25) evolves from Pichu (172), a
        // species with a HIGHER dex number — so this is the exact order
        // Task 6's real output would hand the importer. Built by hand in
        // that same order, Pikachu first, and deliberately NOT sorted
        // parent-first here: doing that would prove nothing about whether
        // the importer (and EF Core underneath it) actually handles the
        // hazard, only that the test dodged it.
        IReadOnlyList<SpeciesImport> batch = [Pikachu(), Pichu()];

        await using var db = NewContext();
        var result = await SpeciesImporter.ImportAsync(db, batch, CancellationToken.None);

        Assert.Equal(2, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Unchanged);

        await using var verify = NewContext();
        var pikachu = await verify.SpeciesRows.SingleAsync(s => s.Id == 25);
        Assert.Equal(172, pikachu.EvolvesFromSpeciesId);
        var pichu = await verify.SpeciesRows.SingleAsync(s => s.Id == 172);
        Assert.Null(pichu.EvolvesFromSpeciesId);
    }

    /// <summary>Reads one scalar via a fresh, non-EF connection — Postgres
    /// truth, not the ORM's opinion of it.</summary>
    private async Task<string> XminAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static SpeciesImport Bulbasaur() => new(
        Id: 1,
        Name: "Bulbasaur",
        Slug: "bulbasaur",
        Generation: 1,
        Region: "Kanto",
        Color: "Green",
        Habitat: "Grassland",
        Status: SpeciesStatus.Ordinary,
        Stage: 0,
        EvolvesFrom: null,
        Types: new[] { "Grass", "Poison" },
        EggGroups: new[] { "Monster", "Grass" },
        LocalizedNames: new Dictionary<string, string> { ["en"] = "Bulbasaur", ["ja"] = "フシギダネ" },
        GradientStart: "#78C850",
        GradientEnd: "#A7DB8D");

    private static SpeciesImport Ivysaur() => new(
        Id: 2,
        Name: "Ivysaur",
        Slug: "ivysaur",
        Generation: 1,
        Region: "Kanto",
        Color: "Green",
        Habitat: "Grassland",
        Status: SpeciesStatus.Ordinary,
        Stage: 1,
        EvolvesFrom: 1,
        Types: new[] { "Grass", "Poison" },
        EggGroups: new[] { "Monster", "Grass" },
        LocalizedNames: new Dictionary<string, string> { ["en"] = "Ivysaur" },
        GradientStart: "#78C850",
        GradientEnd: "#A7DB8D");

    private static SpeciesImport Squirtle() => new(
        Id: 7,
        Name: "Squirtle",
        Slug: "squirtle",
        Generation: 1,
        Region: "Kanto",
        Color: "Blue",
        Habitat: "Cave",
        Status: SpeciesStatus.Ordinary,
        Stage: 0,
        EvolvesFrom: null,
        Types: new[] { "Water" },
        EggGroups: new[] { "Monster", "Water 1" },
        LocalizedNames: new Dictionary<string, string> { ["en"] = "Squirtle" },
        GradientStart: "#6890F0",
        GradientEnd: "#9DB7F5");

    private static SpeciesImport Pikachu() => new(
        Id: 25,
        Name: "Pikachu",
        Slug: "pikachu",
        Generation: 1,
        Region: "Kanto",
        Color: "Yellow",
        Habitat: "Forest",
        Status: SpeciesStatus.Ordinary,
        Stage: 1,
        EvolvesFrom: 172,
        Types: new[] { "Electric" },
        EggGroups: new[] { "Field", "Fairy" },
        LocalizedNames: new Dictionary<string, string> { ["en"] = "Pikachu" },
        GradientStart: "#F8D030",
        GradientEnd: "#FAE078");

    private static SpeciesImport Pichu() => new(
        Id: 172,
        Name: "Pichu",
        Slug: "pichu",
        Generation: 2,
        Region: "Johto",
        Color: "Yellow",
        Habitat: "Forest",
        Status: SpeciesStatus.Ordinary,
        Stage: 0,
        EvolvesFrom: null,
        Types: new[] { "Electric" },
        EggGroups: new[] { "Field", "Fairy" },
        LocalizedNames: new Dictionary<string, string> { ["en"] = "Pichu" },
        GradientStart: "#F8D030",
        GradientEnd: "#FAE078");
}
