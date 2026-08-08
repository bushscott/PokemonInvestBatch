using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PokemonInvestBatch.Domain.Parsing;

/// <summary>
/// A fingerprint of a page: the names of things, never their values. Prices,
/// dates, and sales change every visit; the names should not — a hash never
/// seen before means the page is built from parts in a combination we have
/// not recorded, which archives the HTML for later inspection.
///
/// Note what this does *not* claim to be: a description of layout. The
/// fingerprint also captures how much data the card happens to carry, so a
/// quiet promo with one price tier fingerprints differently from a busy
/// vintage card with six. That is why a new hash alone is not news — see
/// <see cref="FingerprintVocabulary"/> for the question worth alerting on.
/// </summary>
public sealed partial record PageFingerprint
{
    public required string Hash { get; init; }

    /// <summary>The names, by bucket, serialized — the hashed payload.</summary>
    public required string Names { get; init; }

    public static PageFingerprint OfCardDetailPage(string html)
    {
        var byBucket = new SortedDictionary<string, string[]>
        {
            ["vgpc"] = Sorted(VgpcAssignments().Matches(html).Select(m => m.Groups[1].Value)),
            ["chart_data"] = JsonKeys(html, "chart_data"),
            ["pop_data"] = JsonKeys(html, "pop_data"),
            ["auction_tiers"] = Sorted(AuctionTierTokens().Matches(html).Select(m => m.Value)),
        };

        var names = JsonSerializer.Serialize(byBucket);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(names)));
        return new PageFingerprint { Hash = hash, Names = names };
    }

    private static string[] Sorted(IEnumerable<string> values) => [.. values.Distinct().Order()];

    private static string[] JsonKeys(string html, string name)
    {
        using var json = VgpcData.ExtractObject(html, name);
        return json is null
            ? []
            : Sorted(json.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [GeneratedRegex(@"VGPC\.(\w+)\s*=")]
    private static partial Regex VgpcAssignments();

    [GeneratedRegex("completed-auctions-[a-z-]+")]
    private static partial Regex AuctionTierTokens();
}
