namespace PokemonInvestBatch.Application.Pokedex;

/// <summary>
/// One node of a species evolution chain — the recursive shape
/// <c>evolution-chain/{n}.json</c>'s <c>chain</c>/<c>chain.evolves_to[]</c>
/// carries, reduced to exactly what stage derivation needs: the species id
/// this node names (parsed by <see cref="PokeapiDataset"/> from the node's
/// <c>species.url</c>, not its display name, so matching never depends on
/// string casing or diacritics) and its direct evolutions. Building this
/// tree from JSON is <see cref="PokeapiDataset"/>'s job; walking it is
/// <see cref="PokedexMaps.Stage"/>'s.
/// </summary>
/// <param name="SpeciesId">The national dex number this node names.</param>
/// <param name="EvolvesTo">This node's direct evolutions — empty at a leaf.</param>
public sealed record EvolutionChainNode(int SpeciesId, IReadOnlyList<EvolutionChainNode> EvolvesTo);

/// <summary>
/// The hand-authored reference tables the pinned PokéAPI dataset does not
/// itself carry in a display-ready form (ADR-0011), plus the evolution-chain
/// depth walk that derives a species' stage. Pure — no I/O, no clock, no
/// randomness. Every lookup is a total function over its authored table with
/// one throw arm for the input it does not cover: a species carrying a
/// generation, egg group, or type these tables were never taught is
/// reference-data drift, and drift fails loudly here rather than rendering a
/// blank or a guess (spec §6).
/// </summary>
public static class PokedexMaps
{
    /// <summary>Maps a generation number to the region CardStock displays —
    /// Kanto (1) through Paldea (9), every generation the pinned dataset
    /// currently defines. Anything else throws: a tenth generation is a
    /// dataset re-pin this table has not been taught yet, not a case to
    /// default toward.</summary>
    public static string Region(short generation) => generation switch
    {
        1 => "Kanto",
        2 => "Johto",
        3 => "Hoenn",
        4 => "Sinnoh",
        5 => "Unova",
        6 => "Kalos",
        7 => "Alola",
        8 => "Galar",
        9 => "Paldea",
        _ => throw new InvalidOperationException(
            $"PokedexMaps.Region: unmapped generation '{generation}'."),
    };

    /// <summary>Maps a species' PokéAPI egg-group resource name to the
    /// display name CardStock's Character page shows. This table is the
    /// <em>only</em> source for egg-group display names (controller ruling)
    /// — the dataset's own localized-name arrays are never consulted, even
    /// though they carry an English name too, so this list is the one place
    /// that can ever disagree with PokéAPI's own wording. Three of the
    /// fifteen rows rename outright on purpose: "ground" reads as "Field",
    /// "plant" as "Grass", "humanshape" as "Human-Like" — the games'
    /// familiar group names, not PokéAPI's internal resource ids. Anything
    /// else throws.</summary>
    public static string EggGroupDisplay(string apiName) => apiName switch
    {
        "monster" => "Monster",
        "water1" => "Water 1",
        "water2" => "Water 2",
        "water3" => "Water 3",
        "bug" => "Bug",
        "flying" => "Flying",
        "ground" => "Field",
        "fairy" => "Fairy",
        "plant" => "Grass",
        "humanshape" => "Human-Like",
        "mineral" => "Mineral",
        "indeterminate" => "Amorphous",
        "ditto" => "Ditto",
        "dragon" => "Dragon",
        "no-eggs" => "No eggs",
        _ => throw new InvalidOperationException(
            $"PokedexMaps.EggGroupDisplay: unmapped egg group '{apiName}'."),
    };

    /// <summary>Maps a species' primary type — Capitalized, e.g. "Dark" —
    /// to the two-stop hex gradient its identity header and tiles render
    /// (CardStock D-104). Eighteen rows, one per type the dataset defines.
    /// Every pair is a hand-picked, tasteful gradient rather than a computed
    /// tint; Dark's pair is the existing Umbreon pair already live in the
    /// CardStock prototypes, carried over exactly rather than re-derived.
    /// Anything else throws: a nineteenth type is drift, not a case to fall
    /// back to a default gradient.</summary>
    public static (string Start, string End) TypeGradient(string primaryType) => primaryType switch
    {
        "Fire" => ("#B4522A", "#E8A46B"),
        "Water" => ("#3D6FA8", "#8FC1E8"),
        "Grass" => ("#3F7A4A", "#9BC98F"),
        "Electric" => ("#B08A1E", "#EAD06B"),
        "Psychic" => ("#7A4E8F", "#C79BD6"),
        "Dark" => ("#2B2D42", "#5C6B9E"),
        "Dragon" => ("#4A5AA8", "#8FA0E0"),
        "Fairy" => ("#A85A88", "#E0A8C8"),
        "Normal" => ("#8A8A86", "#C9C9C4"),
        "Fighting" => ("#8F4E3A", "#D69B7A"),
        "Flying" => ("#6E8AB8", "#B8CCE8"),
        "Poison" => ("#6E4E8F", "#B08AC9"),
        "Ground" => ("#8F7A4E", "#D6C08A"),
        "Rock" => ("#7A6E5A", "#B8AC94"),
        "Bug" => ("#6E8F3A", "#B8D68A"),
        "Ghost" => ("#4E4E7A", "#9494C9"),
        "Steel" => ("#6E7A8A", "#B0BCC9"),
        "Ice" => ("#5A9BB8", "#B0E0F0"),
        _ => throw new InvalidOperationException(
            $"PokedexMaps.TypeGradient: unmapped type '{primaryType}'."),
    };

    /// <summary>Depth of <paramref name="speciesId"/> from <paramref
    /// name="root"/> — root itself is stage 0, its direct evolutions stage
    /// 1, and so on (spec §3). Walks <see cref="EvolutionChainNode.EvolvesTo"/>
    /// depth-first; a chain never branches back together (evolution is a
    /// tree, not a graph), so the first match found is the only one that
    /// exists. Throws if the id appears nowhere in the chain — every species
    /// that names a chain id is expected to be a node in it, and returning a
    /// fallback stage would misrepresent where in its line it actually
    /// sits.</summary>
    public static short Stage(EvolutionChainNode root, int speciesId)
        => FindDepth(root, speciesId, 0)
            ?? throw new InvalidOperationException(
                $"PokedexMaps.Stage: species {speciesId} not found in the evolution chain rooted at species {root.SpeciesId}.");

    private static short? FindDepth(EvolutionChainNode node, int speciesId, short depth)
    {
        if (node.SpeciesId == speciesId)
        {
            return depth;
        }

        foreach (var child in node.EvolvesTo)
        {
            var found = FindDepth(child, speciesId, (short)(depth + 1));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
