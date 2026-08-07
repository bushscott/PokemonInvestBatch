using System.Text.Json;

namespace PokemonInvestBatch.Domain.Parsing;

/// <summary>
/// Every name a shape uses, qualified by the bucket it appeared in
/// ("chart_data:psa10").
///
/// The hash asks whether we have seen this exact combination before, and on
/// this site the answer is routinely no for innocent reasons: a shape counts
/// how much data a card carries, not only how the page is built. A promo with
/// one price tier and no census is a combination never seen and a card in
/// perfect health. The vocabulary asks the question the alarm is actually
/// for — is there a name here we have never seen anywhere? An unaccounted
/// name is the site's markup moving. A smaller helping of familiar names is
/// just a quiet card.
/// </summary>
public static class PageShapeVocabulary
{
    /// <summary>Every "bucket:name" pair the shape carries, in order.</summary>
    public static IReadOnlyCollection<string> Of(string shapeJson)
    {
        using var document = JsonDocument.Parse(shapeJson);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var bucket in document.RootElement.EnumerateObject())
        {
            foreach (var name in bucket.Value.EnumerateArray())
            {
                names.Add($"{bucket.Name}:{name.GetString()}");
            }
        }

        return names;
    }

    /// <summary>
    /// The names this shape uses that appear in none of the shapes already
    /// known. Empty means a new arrangement of familiar parts — worth
    /// archiving, but there is nothing to tell anyone.
    /// </summary>
    public static IReadOnlyCollection<string> NamesAbsentFrom(
        string shapeJson, IEnumerable<string> knownShapes)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var shape in knownShapes)
        {
            known.UnionWith(Of(shape));
        }

        return [.. Of(shapeJson).Where(name => !known.Contains(name))];
    }
}
