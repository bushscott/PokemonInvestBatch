namespace PokemonInvestBatch.Domain.Parsing;

/// <summary>
/// Pure parser for a pricecharting.com card detail (<c>/game/...</c>) page.
/// HTML in, facts out — no I/O, no clock, no database.
/// </summary>
public static class CardDetailParser
{
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

    public static CardDetailPage Parse(string html) =>
        new()
        {
            Chart = ParseChart(html),
            Population = ParsePopulation(html),
        };

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
