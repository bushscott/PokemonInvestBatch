using System.Text.Json.Nodes;
using PokemonInvestBatch.Application.Pokedex;

namespace PokemonInvestBatch.Application.Tests.Pokedex;

public class PokeapiDatasetTests : IDisposable
{
    // Real, trimmed PokéAPI JSON (pinned commit, ScraperOptions.PokeapiDataPin)
    // copied to the test output directory by the .csproj's CopyToOutputDirectory
    // item — see PokemonInvestBatch.Application.Tests.csproj.
    private static readonly string FixturesDirectory =
        Path.Combine(AppContext.BaseDirectory, "Pokedex", "Fixtures");

    // Doctored-mirror directories built by BuildMirror for the throw tests,
    // cleaned up after each test — the checked-in Fixtures stay untouched.
    private readonly List<string> _mirrorDirectories = [];

    public void Dispose()
    {
        foreach (var directory in _mirrorDirectories)
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Loads_Umbreon_with_the_full_expected_tuple()
    {
        var species = PokeapiDataset.Load(FixturesDirectory);

        var umbreon = species.Single(s => s.Id == 197);

        Assert.Equal("Umbreon", umbreon.Name);
        Assert.Equal("umbreon", umbreon.Slug);
        Assert.Equal(2, umbreon.Generation);
        Assert.Equal("Johto", umbreon.Region);
        Assert.Equal("Black", umbreon.Color);
        Assert.Equal("Urban", umbreon.Habitat);
        Assert.Equal(SpeciesStatus.Ordinary, umbreon.Status);
        Assert.Equal(1, umbreon.Stage);
        Assert.Equal(133, umbreon.EvolvesFrom);
        Assert.Equal(new[] { "Dark" }, umbreon.Types);
        Assert.Equal(new[] { "Field" }, umbreon.EggGroups);
        Assert.True(umbreon.LocalizedNames.ContainsKey("ja"));
        Assert.False(string.IsNullOrEmpty(umbreon.GradientStart));
        Assert.False(string.IsNullOrEmpty(umbreon.GradientEnd));

        // The exact Dark pair from the map, not just "non-empty".
        var (expectedStart, expectedEnd) = PokedexMaps.TypeGradient("Dark");
        Assert.Equal(expectedStart, umbreon.GradientStart);
        Assert.Equal(expectedEnd, umbreon.GradientEnd);
    }

    [Fact]
    public void Pikachu_derives_the_pinned_baby_case_stage_and_evolves_from()
    {
        // Pichu(172) is the chain root even though it debuted a generation
        // after Pikachu(25) — spec §3's pinned baby-case: chain root
        // predates the species that evolves into it.
        var species = PokeapiDataset.Load(FixturesDirectory);

        var pikachu = species.Single(s => s.Id == 25);

        Assert.Equal(1, pikachu.Stage);
        Assert.Equal(172, pikachu.EvolvesFrom);
    }

    [Fact]
    public void Type_Null_has_no_habitat()
    {
        var species = PokeapiDataset.Load(FixturesDirectory);

        var typeNull = species.Single(s => s.Id == 772);

        Assert.Null(typeNull.Habitat);
    }

    // Two of PokéAPI's nine pokemon-habitat values are hyphenated compounds
    // (verified against the live pokemon-habitat index, not just the six
    // checked-in fixture species — none of which carries either). A
    // single-word Capitalize would only fix the first segment and ship
    // "Rough-terrain" / "Waters-edge" to Postgres and the UI with nothing
    // downstream correcting the casing.
    [Theory]
    [InlineData("rough-terrain", "Rough-Terrain")]
    [InlineData("waters-edge", "Waters-Edge")]
    public void A_hyphenated_habitat_capitalizes_every_segment(string apiHabitat, string expectedDisplay)
    {
        var mirror = BuildMirror(mutateSpecies: umbreon => umbreon["habitat"]!["name"] = apiHabitat);

        var species = PokeapiDataset.Load(mirror);

        Assert.Equal(expectedDisplay, species.Single(s => s.Id == 197).Habitat);
    }

    [Fact]
    public void Load_orders_results_by_id()
    {
        // The fixture file names ("133", "172", "197", "25", "26", "772")
        // sort out of numeric order as strings, so this only passes if Load
        // actually sorts by Id rather than trusting enumeration order.
        var species = PokeapiDataset.Load(FixturesDirectory);

        Assert.Equal(new[] { 25, 26, 133, 172, 197, 772 }, species.Select(s => s.Id));
    }

    [Fact]
    public void An_unmapped_egg_group_throws_naming_the_group()
    {
        var mirror = BuildMirror(mutateSpecies: umbreon =>
            umbreon["egg_groups"]![0]!["name"] = "chien-pao-group");

        var ex = Assert.Throws<InvalidOperationException>(() => PokeapiDataset.Load(mirror));

        Assert.Contains("chien-pao-group", ex.Message);
        Assert.Contains("pokemon-species/197.json", ex.Message);
    }

    [Fact]
    public void An_unmapped_type_throws()
    {
        var mirror = BuildMirror(mutatePokemon: umbreon =>
            umbreon["types"]![0]!["type"]!["name"] = "stellar");

        var ex = Assert.Throws<InvalidOperationException>(() => PokeapiDataset.Load(mirror));

        Assert.Contains("Stellar", ex.Message);
        Assert.Contains("pokemon/197.json", ex.Message);
    }

    [Fact]
    public void A_default_variety_with_zero_types_throws_naming_the_file()
    {
        var mirror = BuildMirror(mutatePokemon: umbreon => umbreon["types"] = new JsonArray());

        var ex = Assert.Throws<InvalidOperationException>(() => PokeapiDataset.Load(mirror));

        Assert.Contains("pokemon/197.json", ex.Message);
    }

    [Fact]
    public void An_unmapped_generation_throws()
    {
        var mirror = BuildMirror(mutateSpecies: umbreon =>
            umbreon["generation"]!["name"] = "generation-omega");

        var ex = Assert.Throws<InvalidOperationException>(() => PokeapiDataset.Load(mirror));

        Assert.Contains("generation-omega", ex.Message);
        Assert.Contains("pokemon-species/197.json", ex.Message);
    }

    [Fact]
    public void A_species_missing_an_english_name_throws()
    {
        var mirror = BuildMirror(mutateSpecies: umbreon =>
        {
            var enEntry = umbreon["names"]!.AsArray()
                .Single(entry => entry!["language"]!["name"]!.GetValue<string>() == "en");
            enEntry!["language"]!["name"] = "en-doctored";
        });

        var ex = Assert.Throws<InvalidOperationException>(() => PokeapiDataset.Load(mirror));

        Assert.Contains("pokemon-species/197.json", ex.Message);
        Assert.Contains("names[en]", ex.Message);
    }

    /// <summary>Builds a one-species mirror (Umbreon 197, evolution chain
    /// 67) from the checked-in fixtures, optionally mutating the parsed
    /// species and/or pokemon JSON before writing it out — the "doctored
    /// copy of a real file, written to a temp dir" the brief calls for, so
    /// the checked-in fixtures stay real and untouched.</summary>
    private string BuildMirror(Action<JsonNode>? mutateSpecies = null, Action<JsonNode>? mutatePokemon = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pokeapi-mirror-{Guid.NewGuid():N}");
        _mirrorDirectories.Add(directory);
        Directory.CreateDirectory(Path.Combine(directory, "pokemon-species"));
        Directory.CreateDirectory(Path.Combine(directory, "pokemon"));
        Directory.CreateDirectory(Path.Combine(directory, "evolution-chain"));

        var species = JsonNode.Parse(
            File.ReadAllText(Path.Combine(FixturesDirectory, "pokemon-species", "197.json")))!;
        mutateSpecies?.Invoke(species);
        File.WriteAllText(Path.Combine(directory, "pokemon-species", "197.json"), species.ToJsonString());

        var pokemon = JsonNode.Parse(
            File.ReadAllText(Path.Combine(FixturesDirectory, "pokemon", "197.json")))!;
        mutatePokemon?.Invoke(pokemon);
        File.WriteAllText(Path.Combine(directory, "pokemon", "197.json"), pokemon.ToJsonString());

        File.Copy(
            Path.Combine(FixturesDirectory, "evolution-chain", "67.json"),
            Path.Combine(directory, "evolution-chain", "67.json"));

        return directory;
    }
}
