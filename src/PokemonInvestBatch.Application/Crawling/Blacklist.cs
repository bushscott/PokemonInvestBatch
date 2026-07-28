using System.Text.Json;

namespace PokemonInvestBatch.Application.Crawling;

/// <summary>
/// The user-maintained set blacklist: an array of { slug, reason } objects,
/// re-read each enumeration cycle so edits apply without a restart.
/// </summary>
public sealed class Blacklist
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HashSet<string> _slugs;

    private Blacklist(IEnumerable<string> slugs) => _slugs = [.. slugs];

    /// <summary>Throws on malformed JSON: a broken blacklist silently ignored
    /// would crawl sets the user explicitly excluded.</summary>
    public static Blacklist Parse(string json)
    {
        var entries = JsonSerializer.Deserialize<List<Entry>>(json, SerializerOptions)
            ?? throw new JsonException("Blacklist JSON is null.");
        return new Blacklist(entries.Select(e => e.Slug));
    }

    public bool Contains(string slug) => _slugs.Contains(slug);

    private sealed record Entry
    {
        public required string Slug { get; init; }

        public string? Reason { get; init; }
    }
}
