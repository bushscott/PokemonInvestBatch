using System.Text.Json;

namespace PokemonInvestBatch.Application.Enrichment;

/// <summary>
/// Parser for the hand-curated series→era file (repo root
/// tcgdex-series-eras.json — user input, same posture as
/// tcgdex-set-aliases.json and blacklist.json): TCGdex serie display names
/// ("Sword &amp; Shield") to the product era CardStock's Card page groups
/// sets by ("SWSH"). Re-read every sweep so curation lands without a
/// redeploy; malformed content refuses loudly rather than silently leaving
/// a whole era's sets without one. A serie this file does not name simply
/// resolves to a null era (the set-details sweep's lookup, not this
/// parser's job) — the file is expected to cover the mainline chronological
/// eras only, not every serie TCGdex's catalog groups sets under (trainer
/// kits and McDonald's promos, among others, have no era and are not
/// expected to).
/// </summary>
public static class TcgdexSeriesEras
{
    /// <summary>Serie display name → era code, verbatim from the file (no
    /// fixed vocabulary is enforced here — the file is the single source
    /// for what the current era set is).</summary>
    public static IReadOnlyDictionary<string, string> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var eras = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.Length == 0)
            {
                throw new InvalidOperationException("A series→era entry has an empty series name.");
            }

            var era = property.Value.GetString();
            if (string.IsNullOrEmpty(era))
            {
                throw new InvalidOperationException($"Series '{property.Name}' maps to an empty era.");
            }

            if (!eras.TryAdd(property.Name, era))
            {
                throw new InvalidOperationException($"Duplicate series→era entry for '{property.Name}'.");
            }
        }

        return eras;
    }
}
