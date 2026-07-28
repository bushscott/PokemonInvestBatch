using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace PokemonInvestBatch.Domain.Parsing;

/// <summary>Pure parser for a pricecharting.com set (<c>/console/...</c>) page.</summary>
public static class ConsolePageParser
{
    public static ConsolePage Parse(string html)
    {
        var document = new HtmlParser().ParseDocument(html);

        return new ConsolePage
        {
            Products = [.. document.QuerySelectorAll("tr[id^='product-']").Select(ReadProduct)],
            NextPageForm = ReadNextPageForm(document),
        };
    }

    private static ProductListing ReadProduct(IElement row)
    {
        var anchor = row.QuerySelector("td.title a")
            ?? throw new SchemaDriftException($"Product row '{row.Id}' has no title link.");
        var href = anchor.GetAttribute("href")
            ?? throw new SchemaDriftException($"Product row '{row.Id}' title link has no href.");

        var idText = row.Id!["product-".Length..];
        if (!long.TryParse(idText, out var productId))
        {
            throw new SchemaDriftException($"Product row id '{row.Id}' is not 'product-{{number}}'.");
        }

        return new ProductListing
        {
            ProductId = productId,
            Url = href,
            Name = anchor.TextContent.Trim(),
        };
    }

    /// <summary>The site's own "more results" POST form, re-sent verbatim; null on the last page.</summary>
    private static Dictionary<string, string>? ReadNextPageForm(IDocument document)
    {
        var form = document.QuerySelector("form.js-next-page");
        if (form is null)
        {
            return null;
        }

        return form.QuerySelectorAll("input[type='hidden']")
            .Where(i => i.GetAttribute("name") is not null)
            .ToDictionary(
                i => i.GetAttribute("name")!,
                i => i.GetAttribute("value") ?? string.Empty);
    }
}
