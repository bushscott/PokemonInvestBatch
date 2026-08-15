using System.Globalization;
using System.Text.Json;

namespace PokemonInvestBatch.Application.Pokedex;

/// <summary>
/// One species, ready for the (later) Postgres importer to upsert into
/// <c>species</c>/<c>species_types</c>/<c>species_egg_groups</c>/
/// <c>species_names</c> (ADR-0011). Every field is already resolved to
/// display form — region, egg-group display names, Capitalized types, and
/// the gradient pair — so the importer performs no lookup of its own; it
/// only writes what <see cref="PokeapiDataset.Load"/> hands it.
/// </summary>
public sealed record SpeciesImport(
    int Id, string Name, string Slug, short Generation, string Region, string Color,
    string? Habitat, SpeciesStatus Status, short Stage, int? EvolvesFrom,
    IReadOnlyList<string> Types, IReadOnlyList<string> EggGroups,
    IReadOnlyDictionary<string, string> LocalizedNames,
    string GradientStart, string GradientEnd);

/// <summary>
/// Reads a pinned PokéAPI dataset mirror into import records (ADR-0011).
/// Pure — no network, no database, no clock; the mirror directory on disk is
/// the only input. Expects Task 7's mirror layout: flat
/// <c>pokemon-species/{dexNumber}.json</c>, <c>pokemon/{pokemonId}.json</c>
/// and <c>evolution-chain/{chainId}.json</c> files directly under one root —
/// flatter than upstream's own <c>{resource}/{n}/index.json</c> shape.
///
/// Every field PokéAPI expresses as a controlled vocabulary (generation, egg
/// group, primary type) is resolved through <see cref="PokedexMaps"/> rather
/// than passed through raw. Every resolution failure, plus a species missing
/// an English display name, throws <see cref="InvalidOperationException"/>
/// naming the source file and the field (spec §6): reference-data drift — a
/// type or egg group PokéAPI adds after these tables were authored — must
/// fail loudly, never render a blank or a guess.
/// </summary>
public static class PokeapiDataset
{
    /// <summary>Reads every <c>pokemon-species/*.json</c> file under
    /// <paramref name="mirrorDirectory"/>, joining each to its default
    /// variety's <c>pokemon/{id}.json</c> (for types) and its
    /// <c>evolution-chain/{id}.json</c> (for stage), and returns the results
    /// ordered by <see cref="SpeciesImport.Id"/>.</summary>
    public static IReadOnlyList<SpeciesImport> Load(string mirrorDirectory)
    {
        var speciesDirectory = Path.Combine(mirrorDirectory, "pokemon-species");
        var results = new List<SpeciesImport>();

        foreach (var speciesPath in Directory.EnumerateFiles(speciesDirectory, "*.json"))
        {
            results.Add(LoadSpecies(mirrorDirectory, speciesPath));
        }

        return results.OrderBy(species => species.Id).ToList();
    }

    private static SpeciesImport LoadSpecies(string mirrorDirectory, string speciesPath)
    {
        var file = $"pokemon-species/{Path.GetFileName(speciesPath)}";
        using var document = JsonDocument.Parse(File.ReadAllText(speciesPath));
        var root = document.RootElement;

        var id = RequireInt(root, "id", file);
        var slug = RequireString(root, "name", file);

        var namesElement = Require(root, "names", file);
        var name = EnglishName(namesElement, file);
        var localizedNames = LocalizedNames(namesElement, file);

        var generationName = RequireString(Require(root, "generation", file), "name", file);
        var generation = ParseGeneration(generationName, file);
        var region = Mapped(file, "generation", generationName, () => PokedexMaps.Region(generation));

        var color = Capitalize(RequireString(Require(root, "color", file), "name", file));

        var habitatElement = Require(root, "habitat", file);
        var habitat = habitatElement.ValueKind == JsonValueKind.Null
            ? null
            : Capitalize(RequireString(habitatElement, "name", file));

        // Mythical wins if a species ever sets both is_legendary and
        // is_mythical. The pinned dataset never does today (spec §3), but
        // Mythical is the strictly narrower, rarer classification — every
        // mythical Pokémon reads as "legendary" in casual usage, never the
        // reverse — so it is the more informative label if a future re-pin
        // ever disagrees.
        var isLegendary = RequireBool(root, "is_legendary", file);
        var isMythical = RequireBool(root, "is_mythical", file);
        var status = isMythical ? SpeciesStatus.Mythical
            : isLegendary ? SpeciesStatus.Legendary
            : SpeciesStatus.Ordinary;

        var evolvesFromElement = Require(root, "evolves_from_species", file);
        int? evolvesFrom = evolvesFromElement.ValueKind == JsonValueKind.Null
            ? null
            : ParseTrailingId(RequireString(evolvesFromElement, "url", file), file, "evolves_from_species.url");

        var eggGroups = Require(root, "egg_groups", file).EnumerateArray()
            .Select(group => RequireString(group, "name", file))
            .Select(apiName => Mapped(file, "egg_groups", apiName, () => PokedexMaps.EggGroupDisplay(apiName)))
            .ToList();

        var chainUrl = RequireString(Require(root, "evolution_chain", file), "url", file);
        var chainId = ParseTrailingId(chainUrl, file, "evolution_chain.url");

        var defaultVarietyId = DefaultVarietyId(root, file);
        var pokemonFile = $"pokemon/{defaultVarietyId}.json";
        var (types, primaryType) = LoadTypes(mirrorDirectory, defaultVarietyId, pokemonFile);
        var (gradientStart, gradientEnd) =
            Mapped(pokemonFile, "type", primaryType, () => PokedexMaps.TypeGradient(primaryType));

        var chainFile = $"evolution-chain/{chainId}.json";
        var chainRoot = LoadEvolutionChain(mirrorDirectory, chainId, chainFile);
        var stage = PokedexMaps.Stage(chainRoot, id);

        return new SpeciesImport(
            id, name, slug, generation, region, color, habitat, status, stage, evolvesFrom,
            types, eggGroups, localizedNames, gradientStart, gradientEnd);
    }

    /// <summary>The id of the species' default variety (<c>varieties[]</c>
    /// entry with <c>is_default: true</c>), parsed from its <c>pokemon.url</c>
    /// — the file to read for types. Every species has exactly one default
    /// variety; a mirror entry without one is a shape this parser refuses
    /// rather than guesses at.</summary>
    private static int DefaultVarietyId(JsonElement root, string file)
    {
        foreach (var variety in Require(root, "varieties", file).EnumerateArray())
        {
            if (RequireBool(variety, "is_default", file))
            {
                var url = RequireString(Require(variety, "pokemon", file), "url", file);
                return ParseTrailingId(url, file, "varieties[].pokemon.url");
            }
        }

        throw new InvalidOperationException($"{file}: no default variety in 'varieties'.");
    }

    /// <summary>Types from <c>pokemon/{pokemonId}.json</c>, ordered by
    /// <c>slot</c> and Capitalized to match <see cref="PokedexMaps.TypeGradient"/>'s
    /// keys. The primary type is the slot-1 entry — the only one the
    /// gradient lookup ever consults.</summary>
    private static (IReadOnlyList<string> Types, string Primary) LoadTypes(
        string mirrorDirectory, int pokemonId, string file)
    {
        var path = Path.Combine(mirrorDirectory, "pokemon", $"{pokemonId}.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var types = Require(document.RootElement, "types", file).EnumerateArray()
            .Select(entry => (
                Slot: RequireInt(entry, "slot", file),
                Name: RequireString(Require(entry, "type", file), "name", file)))
            .OrderBy(entry => entry.Slot)
            .Select(entry => Capitalize(entry.Name))
            .ToList();

        return (types, types[0]);
    }

    /// <summary>Parses <c>evolution-chain/{chainId}.json</c>'s <c>chain</c>
    /// object into an <see cref="EvolutionChainNode"/> tree.</summary>
    private static EvolutionChainNode LoadEvolutionChain(string mirrorDirectory, int chainId, string file)
    {
        var path = Path.Combine(mirrorDirectory, "evolution-chain", $"{chainId}.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return ParseChainNode(Require(document.RootElement, "chain", file), file);
    }

    private static EvolutionChainNode ParseChainNode(JsonElement node, string file)
    {
        var url = RequireString(Require(node, "species", file), "url", file);
        var speciesId = ParseTrailingId(url, file, "chain.species.url");
        var children = Require(node, "evolves_to", file).EnumerateArray()
            .Select(child => ParseChainNode(child, file))
            .ToList();
        return new EvolutionChainNode(speciesId, children);
    }

    /// <summary>The <c>names[]</c> entry whose <c>language.name</c> is
    /// "en" — the species' display name. Loud-throw if absent: every
    /// species this dataset carries names in English, so a file missing one
    /// is drift, not a case to fall back to the slug.</summary>
    private static string EnglishName(JsonElement namesArray, string file)
    {
        foreach (var entry in namesArray.EnumerateArray())
        {
            if (RequireString(Require(entry, "language", file), "name", file) == "en")
            {
                return RequireString(entry, "name", file);
            }
        }

        throw new InvalidOperationException($"{file}: missing field 'names[en]' — no English display name.");
    }

    /// <summary>Every <c>names[]</c> entry as language code → localized
    /// name (ADR-0011's <c>species_names</c> — 12 languages including
    /// Japanese, imported now because the dataset carries it free, unused
    /// by any reader until a later phase).</summary>
    private static IReadOnlyDictionary<string, string> LocalizedNames(JsonElement namesArray, string file)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in namesArray.EnumerateArray())
        {
            var language = RequireString(Require(entry, "language", file), "name", file);
            var localized = RequireString(entry, "name", file);
            map[language] = localized;
        }

        return map;
    }

    /// <summary>Parses a PokéAPI <c>generation.name</c> ("generation-ii")
    /// into its number by mapping the roman numeral suffix i–ix. Anything
    /// else — a tenth generation's numeral, or a shape that does not carry
    /// the "generation-" prefix at all — throws naming this file and the
    /// raw value.</summary>
    private static short ParseGeneration(string generationName, string file)
    {
        const string Prefix = "generation-";
        var roman = generationName.StartsWith(Prefix, StringComparison.Ordinal)
            ? generationName[Prefix.Length..]
            : generationName;

        return roman switch
        {
            "i" => 1,
            "ii" => 2,
            "iii" => 3,
            "iv" => 4,
            "v" => 5,
            "vi" => 6,
            "vii" => 7,
            "viii" => 8,
            "ix" => 9,
            _ => throw new InvalidOperationException($"{file}: unmapped generation '{generationName}'."),
        };
    }

    /// <summary>Extracts the trailing numeric id from a PokéAPI resource url
    /// ("/api/v2/pokemon-species/133/" → 133).</summary>
    private static int ParseTrailingId(string url, string file, string field)
    {
        var trimmed = url.TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        var idText = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;

        return int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : throw new InvalidOperationException(
                $"{file}: field '{field}' ('{url}') does not end in a numeric id.");
    }

    /// <summary>Upper-cases the first character of each '-'-separated
    /// segment and lower-cases the rest, ordinally, rejoining with '-'. Most
    /// of PokéAPI's color/habitat/type names are single lowercase words
    /// ("black", "urban", "dark") and pass through as a one-segment split;
    /// two of the nine habitat values are hyphenated compounds
    /// ("rough-terrain", "waters-edge") and need every segment capitalized
    /// ("Rough-Terrain", "Waters-Edge") — a single-word capitalize would
    /// only fix the first segment and ship "Rough-terrain" to Postgres and
    /// the UI with nothing downstream correcting it. One shared helper for
    /// every caller (color, habitat, type), since the hyphen-aware
    /// transform is a strict superset of the single-word one — colors and
    /// types are never hyphenated, so they are unaffected.</summary>
    private static string Capitalize(string value)
        => string.Join('-', value.Split('-').Select(CapitalizeSegment));

    private static string CapitalizeSegment(string segment)
        => segment.Length == 0 ? segment : char.ToUpperInvariant(segment[0]) + segment[1..].ToLowerInvariant();

    /// <summary>Runs a <see cref="PokedexMaps"/> lookup and rewrites any
    /// failure into an exception naming the source file and field.
    /// PokedexMaps is a pure map with no notion of "which file" — that
    /// context is attached here, once, for every mapped lookup
    /// <see cref="Load"/> performs.</summary>
    private static T Mapped<T>(string file, string field, string rawValue, Func<T> lookup)
    {
        try
        {
            return lookup();
        }
        catch (InvalidOperationException inner)
        {
            throw new InvalidOperationException($"{file}: unmapped {field} '{rawValue}'.", inner);
        }
    }

    private static JsonElement Require(JsonElement element, string field, string file) =>
        element.TryGetProperty(field, out var value)
            ? value
            : throw new InvalidOperationException($"{file}: missing field '{field}'.");

    private static string RequireString(JsonElement element, string field, string file) =>
        Require(element, field, file).GetString()
            ?? throw new InvalidOperationException($"{file}: field '{field}' is null.");

    private static int RequireInt(JsonElement element, string field, string file) =>
        Require(element, field, file).GetInt32();

    private static bool RequireBool(JsonElement element, string field, string file) =>
        Require(element, field, file).GetBoolean();
}
