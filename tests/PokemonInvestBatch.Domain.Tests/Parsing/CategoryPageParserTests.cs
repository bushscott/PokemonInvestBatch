using PokemonInvestBatch.Domain.Parsing;
using PokemonInvestBatch.Domain.Tests.Fixtures;

namespace PokemonInvestBatch.Domain.Tests.Parsing;

public class CategoryPageParserTests
{
    [Fact]
    public void ParseSets_reads_all_unique_sets_from_the_content_block()
    {
        var sets = CategoryPageParser.ParseSets(Fixture.Load("category-pokemon-cards"));

        Assert.Equal(303, sets.Count);

        var baseSet = Assert.Single(sets, s => s.Slug == "pokemon-base-set");
        Assert.Equal("Pokemon Base Set", baseSet.Name);
    }

    [Fact]
    public void ParseSets_scopes_to_the_set_list_not_the_site_nav()
    {
        // The global nav also links /console/pokemon-mini — the handheld
        // console, not a card set. Scraping must target the content block.
        var sets = CategoryPageParser.ParseSets(Fixture.Load("category-pokemon-cards"));

        Assert.DoesNotContain(sets, s => s.Slug == "pokemon-mini");
    }

    [Fact]
    public void ParseSets_decodes_entities_in_slugs()
    {
        var sets = CategoryPageParser.ParseSets(Fixture.Load("category-pokemon-cards"));

        Assert.Contains(sets, s => s.Slug == "pokemon-scarlet-&-violet-151");
        Assert.DoesNotContain(sets, s => s.Slug.Contains("&amp;"));
    }
}
