using System.Text.Json;

namespace PokemonInvestBatch.Domain.Parsing;

/// <summary>
/// Extracts the inline <c>VGPC.*</c> JSON assignments a card detail page
/// embeds for its charts (e.g. <c>VGPC.pop_data = {...};</c>).
/// </summary>
internal static class VgpcData
{
    /// <summary>Returns the JSON object assigned to <c>VGPC.{name}</c>, or null when absent.</summary>
    internal static JsonDocument? ExtractObject(string html, string name)
    {
        var marker = $"VGPC.{name}";
        var index = html.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var start = html.IndexOf('{', index + marker.Length);
        if (start < 0)
        {
            throw new SchemaDriftException($"VGPC.{name} is present but no object literal follows it.");
        }

        var depth = 0;
        for (var i = start; i < html.Length; i++)
        {
            if (html[i] == '{')
            {
                depth++;
            }
            else if (html[i] == '}' && --depth == 0)
            {
                var json = html[start..(i + 1)];
                try
                {
                    return JsonDocument.Parse(json);
                }
                catch (JsonException e)
                {
                    throw new SchemaDriftException($"VGPC.{name} is no longer valid JSON: {e.Message}");
                }
            }
        }

        throw new SchemaDriftException($"VGPC.{name} object literal never closes.");
    }
}
