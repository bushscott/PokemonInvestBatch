using System;
using System.Text;
using System.Text.RegularExpressions;

namespace PokemonInvestBatch.Application.Pokedex;

/// <summary>
/// Normalizes Pokémon card titles and species names to a canonical form for
/// matching. Applied to both card titles and species names by the species
/// matcher so they can be reliably compared.
/// </summary>
public static class TitleNormalizer
{
    /// <summary>
    /// Normalizes a title to canonical form for matching: lowercased, with
    /// trailing card-set markers removed, square-bracketed groups stripped,
    /// diacritics folded (preserving gender glyphs), quotes normalized, and
    /// whitespace collapsed.
    /// </summary>
    public static string Normalize(string title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return string.Empty;
        }

        var result = title;

        // Lowercase (invariant culture) first: ToLowerInvariant reliably folds
        // case for accented Latin-1 letters (e.g. É → é) even under this repo's
        // InvariantGlobalization=true — verified directly — so the diacritic
        // fold table below only has to carry lowercase entries.
        result = result.ToLowerInvariant();

        // Fold diacritics: é → e, ñ → n, etc., preserving ♀ (U+2640) and
        // ♂ (U+2642). See RemoveDiacritics for why this is a manual table
        // rather than string.Normalize(NormalizationForm.FormD).
        result = RemoveDiacritics(result);

        // Strip one trailing #<token> (regex \s*#\S+\s*$)
        result = Regex.Replace(result, @"\s*#\S+\s*$", string.Empty);

        // Remove [...] groups
        result = Regex.Replace(result, @"\[[^\]]*\]", string.Empty);

        // Map U+2010/2011/2012/2013 → '-', U+2018/2019 → straight apostrophe
        result = result
            .Replace("‐", "-")  // U+2010 hyphen
            .Replace("‑", "-")  // U+2011 non-breaking hyphen
            .Replace("‒", "-")  // U+2012 figure dash
            .Replace("–", "-")  // U+2013 en dash
            .Replace('‘', '\'')  // U+2018 left single quotation mark → straight apostrophe
            .Replace('’', '\''); // U+2019 right single quotation mark → straight apostrophe

        // Collapse whitespace runs to one space
        result = Regex.Replace(result, @"\s+", " ");

        // Trim
        result = result.Trim();

        return result;
    }

    /// <summary>
    /// Removes diacritical marks from a string by folding each accented
    /// letter to its base form via <see cref="FoldDiacritic"/>.
    ///
    /// This is a manual table rather than the more general
    /// <c>string.Normalize(NormalizationForm.FormD)</c> + drop-NonSpacingMark
    /// approach because this repo builds with
    /// <c>InvariantGlobalization=true</c> (Directory.Build.props, chosen to
    /// drop the libicu dependency on the Pi). Without ICU data,
    /// <c>Normalize(FormD)</c> does not decompose precomposed characters —
    /// verified directly: under InvariantGlobalization, "é".Normalize(FormD)
    /// stays a single U+00E9 char (category LowercaseLetter); only when ICU
    /// is available does it decompose to U+0065 + U+0301 (category
    /// NonSpacingMark). <b>Do not revert to Normalize(FormD)</b> — it
    /// silently no-ops in this build and every caller lowercases first
    /// (<see cref="Normalize"/>), so this table only needs lowercase entries.
    /// </summary>
    private static string RemoveDiacritics(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            sb.Append(FoldDiacritic(c));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Maps a single lowercase accented Latin-1 letter to its unaccented
    /// base letter. Covers exactly the letters Unicode defines a canonical
    /// decomposition for, so it matches what NFD would strip if
    /// <c>Normalize(FormD)</c> worked under this repo's invariant
    /// globalization — not an approximation of it. æ, ø, ð, þ, ß are
    /// intentionally absent: Unicode gives them no decomposition, so real
    /// NFD would not touch them either. Anything else — including ♀
    /// (U+2640) and ♂ (U+2642) — falls through the default arm unchanged.
    /// </summary>
    private static char FoldDiacritic(char c) => c switch
    {
        'à' or 'á' or 'â' or 'ã' or 'ä' or 'å' => 'a',
        'ç' => 'c',
        'è' or 'é' or 'ê' or 'ë' => 'e',
        'ì' or 'í' or 'î' or 'ï' => 'i',
        'ñ' => 'n',
        'ò' or 'ó' or 'ô' or 'õ' or 'ö' => 'o',
        'ù' or 'ú' or 'û' or 'ü' => 'u',
        'ý' or 'ÿ' => 'y',
        _ => c,
    };
}
