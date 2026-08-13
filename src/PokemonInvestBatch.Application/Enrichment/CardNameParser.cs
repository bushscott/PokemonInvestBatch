namespace PokemonInvestBatch.Application.Enrichment;

/// <summary>
/// The three things PriceCharting packs into a card's display name:
/// "Charizard [Shadowless] #4" is base name "Charizard", variant tag
/// "Shadowless", collector number "4". Sealed product carries no number
/// ("Booster Box [1st Edition]"), and a handful of genuine cards don't
/// either ("Unown [A]", "Ancient Mew").
/// </summary>
public sealed record CardNameParts
{
    /// <summary>The name with tags and number removed — what gets compared
    /// against TCGdex's card name.</summary>
    public required string BaseName { get; init; }

    /// <summary>Bracketed variant tags in order, brackets stripped. Variant
    /// products ([1st Edition], [Reverse Holo]) share one TCGdex card —
    /// number and set size are identical across them — so tags never enter
    /// the join; they are parsed out so the base name compares clean.</summary>
    public IReadOnlyList<string> VariantTags { get; init; } = [];

    /// <summary>The text after the final '#', or null when the name carries
    /// none. Text, never an int: numbers carry prefixes (TG23, SWSH262) and
    /// meaningful leading zeros (svp's 001).</summary>
    public string? Number { get; init; }
}

/// <summary>Pure parser for PriceCharting's card-name convention.</summary>
public static class CardNameParser
{
    public static CardNameParts Parse(string name)
    {
        var tags = new List<string>();
        var withoutTags = ExtractTags(name, tags);

        string? number = null;
        var baseName = withoutTags;
        var hash = withoutTags.LastIndexOf('#');
        if (hash >= 0)
        {
            var candidate = withoutTags[(hash + 1)..].Trim();
            if (candidate.Length > 0 && IsNumberToken(candidate))
            {
                number = candidate;
                baseName = withoutTags[..hash];
            }
        }

        return new CardNameParts
        {
            BaseName = CollapseWhitespace(baseName),
            VariantTags = tags,
            Number = number,
        };
    }

    private static string ExtractTags(string name, List<string> tags)
    {
        var remainder = new System.Text.StringBuilder(name.Length);
        var index = 0;
        while (index < name.Length)
        {
            var open = name.IndexOf('[', index);
            if (open < 0)
            {
                remainder.Append(name, index, name.Length - index);
                break;
            }

            var close = name.IndexOf(']', open + 1);
            if (close < 0)
            {
                // An unclosed bracket is not a tag; keep it literal.
                remainder.Append(name, index, name.Length - index);
                break;
            }

            remainder.Append(name, index, open - index);
            tags.Add(name[(open + 1)..close].Trim());
            index = close + 1;
        }

        return remainder.ToString();
    }

    /// <summary>A collector number is one unbroken token of letters, digits,
    /// and the separators real numbers use (SWSH262, TG23, H14, 151/165).
    /// Anything else after a '#' is prose, not a number.</summary>
    private static bool IsNumberToken(string candidate)
    {
        if (!char.IsAsciiLetterOrDigit(candidate[0]))
        {
            return false;
        }

        foreach (var c in candidate)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '/'))
            {
                return false;
            }
        }

        return true;
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
