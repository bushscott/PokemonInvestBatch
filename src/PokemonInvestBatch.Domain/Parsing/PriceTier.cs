namespace PokemonInvestBatch.Domain.Parsing;

/// <summary>
/// The six chart tiers of a card detail page. Detail-page series keys map as
/// used=Ungraded, cib=Grade7, new=Grade8, graded=Grade9, boxonly=Grade9Half,
/// manualonly=Psa10 — verified against the page's own tab labels. Never reuse
/// this mapping for /console/ set pages, whose identical class names mean
/// different grades.
/// </summary>
public enum PriceTier
{
    Ungraded,
    Grade7,
    Grade8,
    Grade9,
    Grade9Half,
    Psa10,
}
