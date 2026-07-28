using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PokemonInvestBatch.Domain.Parsing;

/// <summary>
/// A structural fingerprint of a page: the names of things, never their
/// values. Prices, dates, and sales change every visit; the shape should
/// not — a hash never seen before is the site telling us it changed.
/// </summary>
public sealed partial record PageFingerprint
{
    public required string Hash { get; init; }

    public required string ShapeJson { get; init; }

    public static PageFingerprint OfCardDetailPage(string html)
    {
        var shape = new SortedDictionary<string, string[]>
        {
            ["vgpc"] = Names(VgpcAssignments().Matches(html).Select(m => m.Groups[1].Value)),
            ["chart_data"] = JsonKeys(html, "chart_data"),
            ["pop_data"] = JsonKeys(html, "pop_data"),
            ["auction_tiers"] = Names(AuctionTierTokens().Matches(html).Select(m => m.Value)),
        };

        var shapeJson = JsonSerializer.Serialize(shape);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(shapeJson)));
        return new PageFingerprint { Hash = hash, ShapeJson = shapeJson };
    }

    private static string[] Names(IEnumerable<string> values) => [.. values.Distinct().Order()];

    private static string[] JsonKeys(string html, string name)
    {
        using var json = VgpcData.ExtractObject(html, name);
        return json is null
            ? []
            : Names(json.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [GeneratedRegex(@"VGPC\.(\w+)\s*=")]
    private static partial Regex VgpcAssignments();

    [GeneratedRegex("completed-auctions-[a-z-]+")]
    private static partial Regex AuctionTierTokens();
}
