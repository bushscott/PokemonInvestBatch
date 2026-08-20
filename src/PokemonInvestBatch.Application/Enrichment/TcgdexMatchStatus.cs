namespace PokemonInvestBatch.Application.Enrichment;

/// <summary>
/// The verdict of one attempt to join a PriceCharting card to the TCGdex
/// catalog. Every card the join has looked at carries exactly one current
/// status — unmatched is a first-class state, never a forced guess, and a
/// card with no verdict row at all has never been attempted, so a consumer
/// can tell "no match" from "not yet tried".
///
/// Values are explicit because they land in Postgres (as smallint, the
/// house convention) and are read by a sibling app: appending is safe,
/// renumbering is a breaking change.
/// </summary>
public enum TcgdexMatchStatus : short
{
    /// <summary>The collector number found exactly one candidate in the
    /// routed TCGdex set(s) and the card name agreed — the enrichment
    /// fields are written.</summary>
    Confirmed = 1,

    /// <summary>The number exists in the routed set but every candidate's
    /// name disagreed beyond the known synonym classes. Nothing is written;
    /// the nearest candidate is recorded so the disagreement can be
    /// reviewed. This is the gate that keeps Celebrations Classic
    /// Collection ("Charizard #4" landing on Celebrations #4, Palkia) from
    /// silently enriching with the wrong card.</summary>
    NameMismatch = 2,

    /// <summary>The set is mapped but no candidate card carries this
    /// number — TCGdex coverage lag on a new set, or a product numbered
    /// outside the set's scheme.</summary>
    NumberNotFound = 3,

    /// <summary>More than one candidate agreed on both number and name —
    /// bare-numbered promos collide across eras this way. Guessing is the
    /// one thing this join never does.</summary>
    Ambiguous = 4,

    /// <summary>The card's name carries no #number, so there is nothing to
    /// join on. Mostly sealed product and hardware, but also genuine cards
    /// the site lists unnumbered (the Unown [A]–[Z] run, Ancient Mew).
    /// Excluded from every coverage denominator.</summary>
    NoNumber = 5,

    /// <summary>The card's set has no TCGdex mapping: the Chinese, Korean,
    /// and Topps partitions (deliberately never name-matched), Japanese sets
    /// no curated alias covers (ADR-0012 — the ja join is alias-only), and
    /// English products TCGdex does not carry (World Championships decks,
    /// merchandise lines).</summary>
    UnmappedSet = 6,

    /// <summary>Japanese only: the collector number matched inside the
    /// hand-aliased ja set, but the species-agreement guard (ADR-0012) has
    /// nothing to vouch with — no species on the PriceCharting side (an
    /// untagged card) or none derivable from the TCGdex ja name (trainers,
    /// items, energy). Nothing is written: a wrong-set trainer collision
    /// would slip through an absence-agreement silently, and guessing is
    /// the one thing this join never does.</summary>
    NoSpeciesGuard = 7,
}
