namespace PokemonInvestBatch.Application.Pokedex;

/// <summary>
/// The verdict of one attempt to tag a card with a Pokédex species from its
/// title (ADR-0011). <c>card_tagging</c> holds one row per taggable card,
/// always, so "not yet attempted" is the absence of a row rather than a
/// status here — every value below is a completed attempt's outcome.
///
/// Values are appended only, never reordered — the same discipline as
/// VisitOutcome: this column is stored as smallint and CardStock reads it
/// directly.
/// </summary>
public enum TagStatus : short
{
    /// <summary>The longest-species-name-first title match (ADR-0011 item 3)
    /// found one to three species, and the link(s) are written to
    /// card_species — a title naming more than one species ("Pikachu &amp;
    /// Zekrom GX") is the point of allowing up to three, not an edge
    /// case.</summary>
    Tagged,

    /// <summary>The title names no species at all. Covers trainers, energy,
    /// items, sealed product and hardware — the majority of the catalog. A
    /// legitimate terminal state, not a failure: most cards are not
    /// Pokémon.</summary>
    NoSpecies,

    /// <summary>Four or more candidate species matched the title. Guessing is
    /// the one thing this join never does; the card is left for manual
    /// review instead of tagged wrong.</summary>
    Quarantined,
}

/// <summary>
/// How a card_species or card_tagging row was produced (ADR-0011 item 7).
/// Manual is operator SQL, the same posture as delisting — the tagging
/// lane's re-tagging pass leaves every Manual row untouched on every run,
/// until another human statement changes it.
/// </summary>
public enum TagMethod : short
{
    /// <summary>The longest-species-name-first title match found the
    /// link.</summary>
    TitleMatch,

    /// <summary>An operator wrote the row by hand. Never written by the
    /// tagging lane, and never overwritten by it.</summary>
    Manual,
}

/// <summary>
/// Whether a set has been joined to its TCGdex-derived era/series/release
/// metadata yet (ADR-0011). Every set gets a set_details row, always —
/// Pending is the "not yet matched" first-class value, the same posture
/// tcgdex_enrichments already established for cards.
/// </summary>
public enum SetMatchStatus : short
{
    /// <summary>The set's name matched the TCGdex mapping (ADR-0009); code,
    /// release date, series and era are written.</summary>
    Matched,

    /// <summary>No TCGdex mapping resolved for this set yet. Code, release
    /// date, series and era are all null.</summary>
    Pending,
}

/// <summary>
/// A species' legendary/mythical classification, imported verbatim from the
/// pinned PokéAPI dataset (ADR-0011).
/// </summary>
public enum SpeciesStatus : short
{
    Ordinary,
    Legendary,
    Mythical,
}
