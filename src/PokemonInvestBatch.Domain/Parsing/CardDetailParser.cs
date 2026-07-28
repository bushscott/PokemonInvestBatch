namespace PokemonInvestBatch.Domain.Parsing;

/// <summary>
/// Pure parser for a pricecharting.com card detail (<c>/game/...</c>) page.
/// HTML in, facts out — no I/O, no clock, no database.
/// </summary>
public static class CardDetailParser
{
    private static readonly string[] KnownGraders = ["psa", "cgc"];

    public static CardDetailPage Parse(string html) =>
        new() { Population = ParsePopulation(html) };

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
