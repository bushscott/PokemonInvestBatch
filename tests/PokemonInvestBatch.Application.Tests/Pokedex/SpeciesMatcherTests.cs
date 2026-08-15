using PokemonInvestBatch.Application.Pokedex;

namespace PokemonInvestBatch.Application.Tests.Pokedex;

public class SpeciesMatcherTests
{
    // Trap fixture (spec §6 verbatim, every family): substring nests
    // (Mew/Mewtwo, Kabuto/Kabutops, Porygon/Porygon2/-Z, the Nidoran
    // family), punctuation (♀/♂, Chien‑Pao, Farfetch'd, Mr. Mime vs Mime
    // Jr., Type: Null), multi-species titles, form/owner prefixes, and a
    // normalization case (Flabébé).
    private static readonly IReadOnlyList<(string Name, int SpeciesId)> Candidates =
        SpeciesMatcher.BuildCandidates(new (int Id, string EnglishName)[]
        {
            (25, "Pikachu"),
            (26, "Raichu"),
            (172, "Pichu"),
            (150, "Mewtwo"),
            (151, "Mew"),
            (140, "Kabuto"),
            (141, "Kabutops"),
            (137, "Porygon"),
            (233, "Porygon2"),
            (474, "Porygon-Z"),
            (29, "Nidoran♀"),
            (32, "Nidoran♂"),
            (30, "Nidorina"),
            (33, "Nidorino"),
            (83, "Farfetch'd"),
            (122, "Mr. Mime"),
            (439, "Mime Jr."),
            (772, "Type: Null"),
            (37, "Vulpix"),
            (197, "Umbreon"),
            (644, "Zekrom"),
            (120, "Staryu"),
            (6, "Charizard"),
            (35, "Clefairy"),
            (669, "Flabébé"),
            (1002, "Chien-Pao"),
        });

    [Theory]
    [InlineData("Mewtwo #10", new[] { 150 })]                       // never also Mew
    [InlineData("Mew #8", new[] { 151 })]
    [InlineData("Kabutops #141", new[] { 141 })]
    [InlineData("Porygon2 #233", new[] { 233 })]
    [InlineData("Porygon-Z [Holo] #474", new[] { 474 })]
    [InlineData("Nidoran♀ #25", new[] { 29 })]
    [InlineData("Nidoran♂ [No Rarity] #32", new[] { 32 })]
    [InlineData("Mime Jr. #439", new[] { 439 })]                    // never Mr. Mime
    [InlineData("Mr. Mime #122", new[] { 122 })]
    [InlineData("Type: Null #772", new[] { 772 })]
    [InlineData("Farfetch’d #27", new[] { 83 })]
    [InlineData("Flabebe #83", new[] { 669 })]                      // title anglicized, dataset accented
    [InlineData("Chien‑Pao #32", new[] { 1002 })]
    [InlineData("Alolan Vulpix #21", new[] { 37 })]                 // form prefix
    [InlineData("Misty's Staryu #26", new[] { 120 })]               // owner prefix
    [InlineData("Dark Charizard #4", new[] { 6 })]
    [InlineData("Pikachu & Zekrom GX #33", new[] { 25, 644 })]      // multi-species, both
    public void Tags(string title, int[] expected)
    {
        var verdict = SpeciesMatcher.Match(title, Candidates);

        Assert.Equal(TagStatus.Tagged, verdict.Status);
        Assert.Equal(expected.OrderBy(id => id), verdict.SpeciesIds.OrderBy(id => id));
    }

    [Theory]
    [InlineData("Professor Oak #88")]
    [InlineData("Rare Candy #85")]
    [InlineData("Charizard Spirit Link #75")]                        // denylist beats the match
    [InlineData("Clefairy Doll #70")]
    [InlineData("Growing Grass Energy #104")]
    public void NoSpecies(string title)
    {
        var verdict = SpeciesMatcher.Match(title, Candidates);

        Assert.Equal(TagStatus.NoSpecies, verdict.Status);
        Assert.Empty(verdict.SpeciesIds);
    }

    [Fact]
    public void Quarantines_FourOrMoreSpeciesInOneTitle()
    {
        var verdict = SpeciesMatcher.Match("Pikachu & Raichu & Mewtwo & Zekrom & Staryu #1", Candidates);

        Assert.Equal(TagStatus.Quarantined, verdict.Status);
        Assert.Equal(
            new[] { 25, 26, 150, 644, 120 }.OrderBy(id => id),
            verdict.SpeciesIds.OrderBy(id => id));
    }

    [Fact]
    public void Three_species_tag_but_a_fourth_tips_it_into_quarantine()
    {
        var three = SpeciesMatcher.Match("Pikachu & Raichu & Zekrom #1", Candidates);
        Assert.Equal(TagStatus.Tagged, three.Status);
        Assert.Equal(new[] { 25, 26, 644 }.OrderBy(id => id), three.SpeciesIds.OrderBy(id => id));

        var four = SpeciesMatcher.Match("Pikachu & Raichu & Zekrom & Staryu #1", Candidates);
        Assert.Equal(TagStatus.Quarantined, four.Status);
        Assert.Equal(new[] { 25, 26, 644, 120 }.OrderBy(id => id), four.SpeciesIds.OrderBy(id => id));
    }

    [Fact]
    public void BuildCandidates_breaks_the_MimeJr_MrMime_tie_ordinally()
    {
        var candidates = SpeciesMatcher.BuildCandidates(new (int Id, string EnglishName)[]
        {
            (122, "Mr. Mime"),
            (439, "Mime Jr."),
        });

        Assert.Equal(new[] { "mime jr.", "mr. mime" }, candidates.Select(c => c.Name));
    }
}
