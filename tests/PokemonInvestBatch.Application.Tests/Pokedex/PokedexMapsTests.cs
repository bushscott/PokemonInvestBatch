using PokemonInvestBatch.Application.Pokedex;

namespace PokemonInvestBatch.Application.Tests.Pokedex;

public class PokedexMapsTests
{
    // Every region row, verbatim (spec's 9-entry table): generation 1-9,
    // Kanto through Paldea.
    [Theory]
    [InlineData((short)1, "Kanto")]
    [InlineData((short)2, "Johto")]
    [InlineData((short)3, "Hoenn")]
    [InlineData((short)4, "Sinnoh")]
    [InlineData((short)5, "Unova")]
    [InlineData((short)6, "Kalos")]
    [InlineData((short)7, "Alola")]
    [InlineData((short)8, "Galar")]
    [InlineData((short)9, "Paldea")]
    public void Region_maps_every_generation(short generation, string expected)
    {
        Assert.Equal(expected, PokedexMaps.Region(generation));
    }

    [Theory]
    [InlineData((short)0)]
    [InlineData((short)10)]
    public void Region_throws_for_an_unmapped_generation(short generation)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PokedexMaps.Region(generation));
        Assert.Contains(generation.ToString(), ex.Message);
    }

    // Every egg-group row, verbatim (spec's 15-entry hand map). Three rename
    // outright: ground->Field, plant->Grass, humanshape->Human-Like.
    [Theory]
    [InlineData("monster", "Monster")]
    [InlineData("water1", "Water 1")]
    [InlineData("water2", "Water 2")]
    [InlineData("water3", "Water 3")]
    [InlineData("bug", "Bug")]
    [InlineData("flying", "Flying")]
    [InlineData("ground", "Field")]
    [InlineData("fairy", "Fairy")]
    [InlineData("plant", "Grass")]
    [InlineData("humanshape", "Human-Like")]
    [InlineData("mineral", "Mineral")]
    [InlineData("indeterminate", "Amorphous")]
    [InlineData("ditto", "Ditto")]
    [InlineData("dragon", "Dragon")]
    [InlineData("no-eggs", "No eggs")]
    public void EggGroupDisplay_maps_every_group(string apiName, string expected)
    {
        Assert.Equal(expected, PokedexMaps.EggGroupDisplay(apiName));
    }

    [Fact]
    public void EggGroupDisplay_throws_for_an_unmapped_group_naming_it()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PokedexMaps.EggGroupDisplay("chien-pao"));

        Assert.Contains("chien-pao", ex.Message);
    }

    // Every gradient row, verbatim (spec's 18-entry hex table). Dark is the
    // existing Umbreon pair already live in the CardStock prototypes.
    [Theory]
    [InlineData("Fire", "#B4522A", "#E8A46B")]
    [InlineData("Water", "#3D6FA8", "#8FC1E8")]
    [InlineData("Grass", "#3F7A4A", "#9BC98F")]
    [InlineData("Electric", "#B08A1E", "#EAD06B")]
    [InlineData("Psychic", "#7A4E8F", "#C79BD6")]
    [InlineData("Dark", "#2B2D42", "#5C6B9E")]
    [InlineData("Dragon", "#4A5AA8", "#8FA0E0")]
    [InlineData("Fairy", "#A85A88", "#E0A8C8")]
    [InlineData("Normal", "#8A8A86", "#C9C9C4")]
    [InlineData("Fighting", "#8F4E3A", "#D69B7A")]
    [InlineData("Flying", "#6E8AB8", "#B8CCE8")]
    [InlineData("Poison", "#6E4E8F", "#B08AC9")]
    [InlineData("Ground", "#8F7A4E", "#D6C08A")]
    [InlineData("Rock", "#7A6E5A", "#B8AC94")]
    [InlineData("Bug", "#6E8F3A", "#B8D68A")]
    [InlineData("Ghost", "#4E4E7A", "#9494C9")]
    [InlineData("Steel", "#6E7A8A", "#B0BCC9")]
    [InlineData("Ice", "#5A9BB8", "#B0E0F0")]
    public void TypeGradient_maps_every_type(string type, string expectedStart, string expectedEnd)
    {
        var (start, end) = PokedexMaps.TypeGradient(type);

        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedEnd, end);
    }

    [Fact]
    public void TypeGradient_throws_for_an_unmapped_type_naming_it()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => PokedexMaps.TypeGradient("Stellar"));

        Assert.Contains("Stellar", ex.Message);
    }

    [Fact]
    public void Stage_of_the_root_is_zero()
    {
        var root = new EvolutionChainNode(1, []);

        Assert.Equal(0, PokedexMaps.Stage(root, 1));
    }

    [Fact]
    public void Stage_walks_a_straight_line_to_its_depth()
    {
        // Pichu(172) -> Pikachu(25) -> Raichu(26), the pinned baby-case
        // chain (spec §3): the chain root predates the species it evolves
        // into by a generation.
        var root = new EvolutionChainNode(172,
        [
            new EvolutionChainNode(25,
            [
                new EvolutionChainNode(26, []),
            ]),
        ]);

        Assert.Equal(0, PokedexMaps.Stage(root, 172));
        Assert.Equal(1, PokedexMaps.Stage(root, 25));
        Assert.Equal(2, PokedexMaps.Stage(root, 26));
    }

    [Fact]
    public void Stage_finds_a_species_down_an_eight_way_branch()
    {
        // Eevee(133)'s chain: eight single-step evolutions off one root —
        // the shape evolution-chain/67.json actually has.
        var root = new EvolutionChainNode(133,
        [
            new EvolutionChainNode(134, []), // Vaporeon
            new EvolutionChainNode(135, []), // Jolteon
            new EvolutionChainNode(136, []), // Flareon
            new EvolutionChainNode(196, []), // Espeon
            new EvolutionChainNode(197, []), // Umbreon
            new EvolutionChainNode(470, []), // Leafeon
            new EvolutionChainNode(471, []), // Glaceon
            new EvolutionChainNode(700, []), // Sylveon
        ]);

        Assert.Equal(1, PokedexMaps.Stage(root, 197));
        Assert.Equal(1, PokedexMaps.Stage(root, 700));
    }

    [Fact]
    public void Stage_throws_when_the_species_is_not_in_the_chain()
    {
        var root = new EvolutionChainNode(1, [new EvolutionChainNode(2, [])]);

        var ex = Assert.Throws<InvalidOperationException>(() => PokedexMaps.Stage(root, 999));

        Assert.Contains("999", ex.Message);
    }
}
