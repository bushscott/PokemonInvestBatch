namespace PokemonInvestBatch.Application.Enrichment;

/// <summary>
/// Folds a display name to the comparable core the join matches on:
/// accents flattened, case gone, every symbol a word gap. "Pokémon Breeder"
/// meets PriceCharting's plain-ASCII "Pokemon Breeder"; "Nidoran♂" meets
/// "Nidoran"; "VStar" meets "VSTAR".
///
/// Deliberately not <see cref="string.Normalize(System.Text.NormalizationForm)"/>:
/// this repo builds with InvariantGlobalization, where Normalize throws on
/// any non-ASCII input. The accents that actually occur in the two catalogs
/// are a handful, so they are folded by explicit map, and every other
/// non-ASCII character becomes a gap — which is exactly right for the
/// gender signs and stray symbols that separate otherwise-equal names.
/// </summary>
public static class NameFold
{
    public static string Fold(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            var folded = FoldChar(c);
            if (folded is null)
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(folded.Value);
        }

        return builder.ToString();
    }

    private static char? FoldChar(char c)
    {
        if (char.IsAsciiLetterOrDigit(c))
        {
            return char.ToUpperInvariant(c);
        }

        return c switch
        {
            'é' or 'è' or 'ê' or 'ë' or 'É' or 'È' or 'Ê' or 'Ë' => 'E',
            'á' or 'à' or 'â' or 'ä' or 'Á' or 'À' or 'Â' or 'Ä' => 'A',
            'í' or 'ì' or 'î' or 'ï' or 'Í' or 'Ì' or 'Î' or 'Ï' => 'I',
            'ó' or 'ò' or 'ô' or 'ö' or 'Ó' or 'Ò' or 'Ô' or 'Ö' => 'O',
            'ú' or 'ù' or 'û' or 'ü' or 'Ú' or 'Ù' or 'Û' or 'Ü' => 'U',
            'ñ' or 'Ñ' => 'N',
            'ç' or 'Ç' => 'C',
            // Everything else — punctuation, gender signs, δ suffixes — is a
            // gap, so names differing only by a symbol still meet.
            _ => null,
        };
    }
}
