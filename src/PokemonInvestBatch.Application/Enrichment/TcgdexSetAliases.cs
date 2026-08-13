using System.Text.Json;

namespace PokemonInvestBatch.Application.Enrichment;

/// <summary>
/// Parser for the hand-curated set-alias file (repo root
/// tcgdex-set-aliases.json — user input, same posture as blacklist.json):
/// PriceCharting sets whose names exact matching cannot bridge, each entry
/// naming its TCGdex target set(s) and the reason. Re-read every sweep so
/// curation lands without a redeploy; malformed content refuses loudly
/// rather than silently unmapping curated sets.
/// </summary>
public static class TcgdexSetAliases
{
    /// <summary>Slug → TCGdex set ids (trainer kits are one PC set over two
    /// TCGdex half-deck sets).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var aliases = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var slug = RequireString(entry, "slug");
            var targets = new List<string>();
            foreach (var target in Require(entry, "tcgdex").EnumerateArray())
            {
                targets.Add(target.GetString() is { Length: > 0 } id
                    ? id
                    : throw new InvalidOperationException($"Alias for '{slug}' has an empty tcgdex id."));
            }

            if (targets.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Alias for '{slug}' names no tcgdex sets — remove the entry instead.");
            }

            if (!aliases.TryAdd(slug, targets))
            {
                throw new InvalidOperationException($"Duplicate alias for '{slug}'.");
            }
        }

        return aliases;
    }

    private static string RequireString(JsonElement element, string property) =>
        Require(element, property).GetString() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"An alias entry has an empty '{property}'.");

    private static JsonElement Require(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? value
            : throw new InvalidOperationException($"An alias entry is missing '{property}'.");
}
