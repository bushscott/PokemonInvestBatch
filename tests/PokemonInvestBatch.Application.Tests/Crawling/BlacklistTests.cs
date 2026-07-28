using PokemonInvestBatch.Application.Crawling;

namespace PokemonInvestBatch.Application.Tests.Crawling;

public class BlacklistTests
{
    [Fact]
    public void Parses_slugs_with_reasons_and_answers_membership()
    {
        const string json = """
            [
              { "slug": "pokemon-japanese-promo", "reason": "not collecting Japanese" },
              { "slug": "pokemon-chinese-promo", "reason": "not collecting Chinese" }
            ]
            """;

        var blacklist = Blacklist.Parse(json);

        Assert.True(blacklist.Contains("pokemon-japanese-promo"));
        Assert.False(blacklist.Contains("pokemon-base-set"));
    }

    [Fact]
    public void An_empty_file_blocks_nothing()
    {
        Assert.False(Blacklist.Parse("[]").Contains("pokemon-base-set"));
    }

    [Fact]
    public void Malformed_json_fails_loudly_rather_than_scraping_everything()
    {
        // A broken blacklist silently ignored would crawl sets the user
        // explicitly excluded.
        Assert.ThrowsAny<Exception>(() => Blacklist.Parse("{not json"));
    }
}
