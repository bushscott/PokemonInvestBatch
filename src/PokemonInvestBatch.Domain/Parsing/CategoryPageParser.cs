using AngleSharp.Html.Parser;

namespace PokemonInvestBatch.Domain.Parsing;

/// <summary>Pure parser for the pokemon-cards category page — set discovery.</summary>
public static class CategoryPageParser
{
    private const string ConsolePrefix = "/console/";

    public static IReadOnlyList<SetListing> ParseSets(string html)
    {
        var document = new HtmlParser().ParseDocument(html);

        // Scope to the content block: the global nav also links /console/
        // pages (game hardware, other categories) that are not card sets.
        var content = document.QuerySelector("#home-page")
            ?? throw new SchemaDriftException(
                "div#home-page is missing — the category page layout has drifted.");

        var seen = new HashSet<string>();
        var sets = new List<SetListing>();
        foreach (var anchor in content.QuerySelectorAll($"a[href^='{ConsolePrefix}']"))
        {
            // AngleSharp yields the entity-decoded href.
            var slug = anchor.GetAttribute("href")![ConsolePrefix.Length..];
            if (seen.Add(slug))
            {
                sets.Add(new SetListing { Slug = slug, Name = anchor.TextContent.Trim() });
            }
        }

        return sets;
    }
}
