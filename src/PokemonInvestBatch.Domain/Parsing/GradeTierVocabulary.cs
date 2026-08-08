namespace PokemonInvestBatch.Domain.Parsing;

/// <summary>
/// Plain meaning: the words a card page uses for its condition options, so we
/// can tell a card page from something else on the same site.
///
/// pricecharting.com sells video games as well as cards, and its chart data
/// reuses the video-game series names — <c>used</c>, <c>cib</c>, <c>new</c> —
/// for both. A console page therefore parses perfectly as a card and files its
/// "complete in box" price under Grade 7. The condition selector is the only
/// place the two differ in words rather than structure: a card offers
/// "Ungraded / Grade 7 / PSA 10", a console offers "Loose / CIB / New".
/// </summary>
public static class GradeTierVocabulary
{
    /// <summary>Every tier label the corpus has ever recorded, measured across
    /// 4.1M sales rows in 409 sets. Graders get added over time — TAG and ACE
    /// are recent — so this list grows.</summary>
    private static readonly HashSet<string> CardTiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ungraded",
        "Grade 1", "Grade 2", "Grade 3", "Grade 4", "Grade 5",
        "Grade 6", "Grade 7", "Grade 8", "Grade 9", "Grade 9.5",
        "PSA 10",
        "CGC 10", "CGC 10 Prist.",
        "BGS 10", "BGS 10 Black",
        "SGC 10",
        "TAG 10",
        "ACE 10",
    };

    /// <summary>
    /// True when a page's condition labels include at least one recognized card
    /// grade.
    ///
    /// Deliberately "any", never "all". Requiring every label to be known would
    /// turn the arrival of a new grading company into a corpus-wide outage: the
    /// day pricecharting adds an eleventh grader, every card page in the catalog
    /// would be declared not-a-card and the whole crawl would bench itself. The
    /// asymmetry is safe because the two vocabularies are disjoint — a console
    /// page offers Loose/CIB/New/Graded/Box Only/Manual Only and contains no
    /// card grade at all, so one match is proof enough.
    /// </summary>
    public static bool LooksLikeCard(IEnumerable<string> labels) =>
        labels.Select(Normalize).Any(CardTiers.Contains);

    /// <summary>The site's option text arrives with nested spans and unclosed
    /// tags, so the same label can reach us as "Box Only" or "Box Only\n (0)"
    /// depending on how the HTML parser repairs it. Compare on the squeezed
    /// form or the vocabulary would miss on whitespace alone.</summary>
    public static string Normalize(string label) =>
        string.Join(' ', label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
