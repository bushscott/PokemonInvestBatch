using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace PokemonInvestBatch.Domain.Parsing;

/// <summary>
/// Pure parser for a pricecharting.com card detail (<c>/game/...</c>) page.
/// HTML in, facts out — no I/O, no clock, no database.
/// </summary>
public static partial class CardDetailParser
{
    [GeneratedRegex(@"images\.pricecharting\.com/([a-z0-9]+)/")]
    private static partial Regex ProductImageUrl();

    private static readonly string[] KnownGraders = ["psa", "cgc"];

    // Detail-page mapping only — /console/ set pages reuse these class names
    // for DIFFERENT grades. Verified against the page's own tab labels.
    private static readonly Dictionary<string, PriceTier> SeriesTiers = new()
    {
        ["used"] = PriceTier.Ungraded,
        ["cib"] = PriceTier.Grade7,
        ["new"] = PriceTier.Grade8,
        ["graded"] = PriceTier.Grade9,
        ["boxonly"] = PriceTier.Grade9Half,
        ["manualonly"] = PriceTier.Psa10,
    };

    private static readonly string[] KnownSources = ["ebay", "tcgplayer", "goldin", "heritage", "pwcc"];

    // The default-visible sales table has no completed-auctions-* wrapper;
    // it corresponds to this option in the condition selector.
    private const string DefaultTierToken = "completed-auctions-used";

    // One selector string, shared by the card check and the tier labeler —
    // two copies would drift apart exactly the way this parser distrusts.
    private const string ConditionOptions = "select#completed-auctions-condition option";

    public static CardDetailPage Parse(string html)
    {
        try
        {
            return ParseCore(html);
        }
        catch (Exception e) when (e is not SchemaDriftException and not NotACardPageException)
        {
            // The contract: page-shaped trouble is ALWAYS SchemaDriftException,
            // so the crawl lane can attribute it to the card and quarantine.
            // Any other type escaping here is retried against the same card
            // forever — a livelock. Full cause preserved for parse_failures.
            //
            // NotACardPageException is exempt for the opposite reason: it is a
            // verdict, not trouble, and rewrapping it as drift here would erase
            // the distinction the whole check exists to draw.
            throw new SchemaDriftException(
                $"Unexpected {e.GetType().Name} while parsing: {e.Message}", e);
        }
    }

    private static CardDetailPage ParseCore(string html)
    {
        var chart = ParseChart(html);
        var population = ParsePopulation(html);

        var document = new HtmlParser().ParseDocument(html);
        AssertUsd(document);
        AssertIsCard(document);
        var image = ProductImageUrl().Match(html);
        return new CardDetailPage
        {
            Chart = chart,
            Population = population,
            Sales = ParseSales(document),
            ImageHash = image.Success ? image.Groups[1].Value : null,
        };
    }

    /// <summary>Every cent stored assumes USD; the server renders USD and
    /// converts client-side, so the header's selected currency is the page's
    /// own statement of what its prices mean. Element verified present and
    /// "USD" in captures from 2024 through 2026. If it ever disappears we
    /// stop writing prices rather than write unprovable ones.</summary>
    private static void AssertUsd(IDocument document)
    {
        var currency = document.QuerySelector("#dropdown_selected_currency")?.TextContent.Trim()
            ?? throw new SchemaDriftException(
                "The currency selector is gone — we can no longer prove prices are USD.");
        if (currency != "USD")
        {
            throw new SchemaDriftException(
                $"Page rendered in {currency}, not USD — every price on it would be stored wrong.");
        }
    }

    /// <summary>
    /// The one question the rest of the parser cannot ask: is this a card at
    /// all? Everything else about a console page is indistinguishable from a
    /// card — same markup, same chart series names — so the page is tried on
    /// the two witnesses it cannot help carrying. The condition selector
    /// testifies whenever the product has sales: cards offer grades, games
    /// offer Loose/CIB. The Genre row testifies even when nothing has ever
    /// sold — "Pokemon Card" against "Arcade" or "Systems" — closing the one
    /// shape the selector cannot see: a game nobody has bought, which would
    /// otherwise write its chart into the corpus in silence.
    /// </summary>
    private static void AssertIsCard(IDocument document)
    {
        var labels = document
            .QuerySelectorAll(ConditionOptions)
            .Where(o => !string.IsNullOrWhiteSpace(o.GetAttribute("value")))
            .Select(o => StripCount(o.TextContent))
            .ToList();
        var genre = document.QuerySelector("td[itemprop='genre']")?.TextContent.Trim();
        var genreSaysCard = genre?.Contains("card", StringComparison.OrdinalIgnoreCase) ?? false;

        if (labels.Count > 0)
        {
            if (GradeTierVocabulary.LooksLikeCard(labels))
            {
                return;
            }

            // Unknown tiers under a card genre is the site changing its words,
            // not a console in the catalog. That is drift — it must reach the
            // drift alarm, never quietly retire real cards one visit at a time.
            if (genreSaysCard)
            {
                throw new SchemaDriftException(
                    $"Condition options [{DescribeLabels(labels)}] contain no known card grade "
                    + $"on a page whose genre is '{genre}' — the tier vocabulary has drifted.");
            }

            throw new NotACardPageException(
                $"Condition options [{DescribeLabels(labels)}] contain no card grade — "
                + "this is a video game, console, or accessory page, not a card.");
        }

        // No sales, no selector: the genre is the only witness left. Silence
        // still acquits — a card nobody has ever sold may render neither — so
        // only an explicit non-card genre convicts.
        if (string.IsNullOrEmpty(genre) || genreSaysCard)
        {
            return;
        }

        throw new NotACardPageException(
            $"Genre '{(genre.Length > 40 ? genre[..40] + "…" : genre)}' is not a card genre, and "
            + "with no completed auctions the page has nothing else to say for itself — "
            + "this is a video game, console, or accessory page, not a card.");
    }

    /// <summary>Labels are squeezed and truncated because the site's unclosed
    /// span tags let one option swallow the rest of the page; the reason goes
    /// to the log and the alert and must stay readable.</summary>
    private static string DescribeLabels(IReadOnlyList<string> labels) =>
        string.Join(", ", labels
            .Select(GradeTierVocabulary.Normalize)
            .Select(l => l.Length > 24 ? l[..24] + "…" : l)
            .Take(8));

    private static IReadOnlyList<SaleRecord> ParseSales(IDocument document)
    {
        var tables = document.QuerySelectorAll("table.hoverable-rows.sortable");
        if (tables.Length == 0)
        {
            return [];
        }

        var tierLabels = ReadTierLabels(document);
        return
        [
            .. tables.SelectMany(table =>
            {
                var label = TierLabelFor(table, tierLabels);
                return table.QuerySelectorAll("tr[id]").Select(row => ReadSale(row, label));
            }),
        ];
    }

    /// <summary>Tier names come from the page's own condition selector, never from code.</summary>
    private static Dictionary<string, string> ReadTierLabels(IDocument document)
    {
        var options = document.QuerySelectorAll(ConditionOptions);
        if (options.Length == 0)
        {
            throw new SchemaDriftException(
                "Sales tables are present but select#completed-auctions-condition is missing — " +
                "grade tiers cannot be labeled.");
        }

        return options
            .Where(o => !string.IsNullOrWhiteSpace(o.GetAttribute("value")))
            .ToDictionary(
                o => o.GetAttribute("value")!,
                o => StripCount(o.TextContent));
    }

    /// <summary>Turns "PSA 10 (30)" into "PSA 10".</summary>
    private static string StripCount(string label)
    {
        var text = label.Trim();
        var paren = text.LastIndexOf(" (", StringComparison.Ordinal);
        return paren > 0 ? text[..paren] : text;
    }

    private static string TierLabelFor(IElement table, Dictionary<string, string> tierLabels)
    {
        var token = Ancestors(table)
            .SelectMany(a => a.ClassList)
            .FirstOrDefault(c => c.StartsWith("completed-auctions-", StringComparison.Ordinal))
            ?? DefaultTierToken;

        return tierLabels.TryGetValue(token, out var label)
            ? label
            : throw new SchemaDriftException(
                $"Sales table wrapper '{token}' has no matching option in the condition selector. " +
                "The tier scheme has drifted.");
    }

    private static IEnumerable<IElement> Ancestors(IElement element)
    {
        for (var parent = element.ParentElement; parent is not null; parent = parent.ParentElement)
        {
            yield return parent;
        }
    }

    private static SaleRecord ReadSale(IElement row, string gradeTier)
    {
        // AngleSharp has already decoded HTML entities in the attribute value.
        var id = row.Id!;
        var separator = id.IndexOf('-');
        var source = separator > 0 ? id[..separator] : id;
        if (!KnownSources.Contains(source))
        {
            throw new SchemaDriftException(
                $"Sale row id '{id}' has unknown marketplace prefix '{source}'; " +
                $"known: [{string.Join(", ", KnownSources)}]. A new marketplace must be mapped, not dropped.");
        }

        var sourceId = id[(separator + 1)..];
        if (sourceId.Length > SaleRecord.MaxSourceIdLength)
        {
            throw new SchemaDriftException(
                $"Sale row id '{source}-…' carries a {sourceId.Length}-char marketplace id; "
                + $"no real marketplace id exceeds {SaleRecord.MaxSourceIdLength}.");
        }

        if (gradeTier.Length > SaleRecord.MaxGradeTierLength)
        {
            throw new SchemaDriftException(
                $"Tier label '{gradeTier[..SaleRecord.MaxGradeTierLength]}…' exceeds "
                + $"{SaleRecord.MaxGradeTierLength} chars; the condition selector has drifted.");
        }

        var date = row.QuerySelector("td.date")?.TextContent.Trim()
            ?? throw new SchemaDriftException($"Sale row '{id}' has no date cell.");
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var soldOn))
        {
            throw new SchemaDriftException($"Sale row '{id}' date '{date}' is not yyyy-MM-dd.");
        }

        var price = row.QuerySelector("td.numeric:not(.listed-price) span.js-price")?.TextContent
            ?? throw new SchemaDriftException($"Sale row '{id}' has no price cell.");

        // Titles are third-party display text, not identity — an absurd length
        // is clipped rather than allowed to bench an otherwise-good card.
        var title = (row.QuerySelector("td.title a")?.TextContent ?? string.Empty).Trim();

        return new SaleRecord
        {
            Source = source,
            SourceId = sourceId,
            SoldOn = soldOn,
            GradeTier = gradeTier,
            PriceCents = ParseCents(price)!.Value,
            ListedPriceCents = ParseCents(row.QuerySelector("td.listed-price")?.TextContent),
            Title = title.Length > SaleRecord.MaxTitleLength
                ? title[..SaleRecord.MaxTitleLength]
                : title,
        };
    }

    /// <summary>Parses "$1,234.56" to cents; blank/nbsp-only cells yield null.</summary>
    private static int? ParseCents(string? text)
    {
        var cleaned = text?.Trim().Trim(' ').TrimStart('$').Replace(",", "");
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return null;
        }

        if (!decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                out var dollars))
        {
            throw new SchemaDriftException($"Price text '{text}' is not a dollar amount we understand.");
        }

        var cents = Math.Round(dollars * 100);
        if (cents > int.MaxValue)
        {
            throw new SchemaDriftException($"Price text '{text}' exceeds what a cents column can hold.");
        }

        return (int)cents;
    }

    private static IReadOnlyDictionary<PriceTier, IReadOnlyList<PricePoint>> ParseChart(string html)
    {
        using var chart = VgpcData.ExtractObject(html, "chart_data")
            ?? throw new SchemaDriftException(
                "VGPC.chart_data is absent — this is not a card detail page we understand.");

        var series = new Dictionary<PriceTier, IReadOnlyList<PricePoint>>();
        foreach (var property in chart.RootElement.EnumerateObject())
        {
            if (!SeriesTiers.TryGetValue(property.Name, out var tier))
            {
                throw new SchemaDriftException(
                    $"chart_data contains unknown series '{property.Name}'; " +
                    $"known: [{string.Join(", ", SeriesTiers.Keys)}]. The chart schema has drifted.");
            }

            series[tier] = [.. property.Value.EnumerateArray().Select(ReadPoint)];
        }

        return series;
    }

    private static PricePoint ReadPoint(System.Text.Json.JsonElement pair)
    {
        var epochMs = pair[0].GetInt64();
        var priceCents = pair[1].GetInt32();

        // Timestamps are month starts in the site's US timezone; in UTC they
        // land a few hours into the 1st, so the UTC date is the month itself.
        var month = DateOnly.FromDateTime(
            DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime);

        return new PricePoint(month, priceCents);
    }

    private static PopulationReport? ParsePopulation(string html)
    {
        using var pop = VgpcData.ExtractObject(html, "pop_data");
        if (pop is null)
        {
            return null;
        }

        var unknown = pop.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .Where(name => !KnownGraders.Contains(name))
            .ToList();

        if (unknown.Count > 0)
        {
            throw new SchemaDriftException(
                $"pop_data contains unknown grader keys [{string.Join(", ", unknown)}]; " +
                $"known: [{string.Join(", ", KnownGraders)}]. The census schema has drifted.");
        }

        return new PopulationReport
        {
            Psa = ReadGrades(pop.RootElement, "psa"),
            Cgc = ReadGrades(pop.RootElement, "cgc"),
        };
    }

    private static int[] ReadGrades(System.Text.Json.JsonElement popData, string grader) =>
        popData.TryGetProperty(grader, out var grades)
            ? [.. grades.EnumerateArray().Select(g => g.GetInt32())]
            : [];
}
