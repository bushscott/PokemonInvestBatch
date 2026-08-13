namespace PokemonInvestBatch.Application.Enrichment;

/// <summary>
/// Collector-number normalization, so the two catalogs' spellings of one
/// number meet: PriceCharting writes "#53" where TCGdex's localId is "053"
/// (SVP promos), and zero-padding varies era by era ("TG04" vs "TG4").
/// </summary>
public static class CollectorNumber
{
    /// <summary>Canonical form both sides agree on: uppercase, with leading
    /// zeros dropped from every digit run ("053" → "53", "TG04" → "TG4",
    /// "SWSH062" → "SWSH62"). A run of only zeros keeps one ("0").</summary>
    public static string Canonical(string number)
    {
        var builder = new System.Text.StringBuilder(number.Length);
        var index = 0;
        while (index < number.Length)
        {
            var c = number[index];
            if (char.IsAsciiDigit(c))
            {
                var start = index;
                while (index < number.Length && char.IsAsciiDigit(number[index]))
                {
                    index++;
                }

                var run = number[start..index].TrimStart('0');
                builder.Append(run.Length > 0 ? run : "0");
                continue;
            }

            builder.Append(char.ToUpperInvariant(c));
            index++;
        }

        return builder.ToString();
    }

    /// <summary>The leading letters, uppercased, or "" for a bare number.
    /// This is the routing key: TG/GG gallery cards live in sibling sets,
    /// SV numbers in shiny vaults, and era-prefixed promos (SWSH262, XY124)
    /// in per-era promo sets.</summary>
    public static string AlphaPrefix(string number)
    {
        var length = 0;
        while (length < number.Length && char.IsAsciiLetter(number[length]))
        {
            length++;
        }

        return number[..length].ToUpperInvariant();
    }
}
