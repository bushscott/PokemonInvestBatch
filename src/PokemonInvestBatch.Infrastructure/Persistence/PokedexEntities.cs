using PokemonInvestBatch.Application.Pokedex;

namespace PokemonInvestBatch.Infrastructure.Persistence;

/// <summary>One Pokédex species (ADR-0011). PK is the national dex number,
/// never generated locally — same posture as Card.Id.</summary>
public class Species
{
    public int Id { get; set; }

    /// <summary>English display name, exactly as PokéAPI names it
    /// ("Nidoran♀").</summary>
    public required string Name { get; set; }

    /// <summary>Route-safe form of the name ("nidoran-f").</summary>
    public required string Slug { get; set; }

    public short Generation { get; set; }

    /// <summary>Derived from PokéAPI's generation and stored, not recomputed
    /// on read ("Johto").</summary>
    public required string Region { get; set; }

    public required string Color { get; set; }

    /// <summary>Null for Generation 4 onward — PokéAPI stopped assigning
    /// habitats after Sinnoh.</summary>
    public string? Habitat { get; set; }

    public SpeciesStatus Status { get; set; }

    /// <summary>Chain depth from the evolution root; 0 = basic.</summary>
    public short Stage { get; set; }

    /// <summary>Null for Stage 0 species, which have no earlier form.</summary>
    public int? EvolvesFromSpeciesId { get; set; }

    /// <summary>"#RRGGBB".</summary>
    public required string GradientStart { get; set; }

    /// <summary>"#RRGGBB", pairs with <see cref="GradientStart"/>.</summary>
    public required string GradientEnd { get; set; }
}

/// <summary>A species' PokéAPI type(s), 1–2 rows ordered by Slot.</summary>
public class SpeciesType
{
    public int SpeciesId { get; set; }

    public short Slot { get; set; }

    public required string Type { get; set; }
}

/// <summary>A species' egg group(s), display-named, 1–2 rows.</summary>
public class SpeciesEggGroup
{
    public int SpeciesId { get; set; }

    public required string EggGroup { get; set; }
}

/// <summary>One row per species per dataset language (12, including
/// Japanese) — imported because the dataset carries it free; unused by any
/// reader until a later phase.</summary>
public class SpeciesName
{
    public int SpeciesId { get; set; }

    public required string Language { get; set; }

    public required string Name { get; set; }
}

/// <summary>Card ↔ species junction. Current-state (ADR-0011 deviation).</summary>
public class CardSpeciesLink
{
    public long CardId { get; set; }

    public int SpeciesId { get; set; }

    public TagMethod Method { get; set; }
}

/// <summary>One row per taggable card, always — "no row" means "not yet
/// attempted", which is what the lane's anti-join hunts.</summary>
public class CardTagging
{
    public long CardId { get; set; }

    public TagStatus Status { get; set; }

    public TagMethod Method { get; set; }

    /// <summary>The exact title text matched against — the rename detector:
    /// the tagging lane's re-tagging pass compares this to the card's
    /// current name to notice a title change without re-deriving every row
    /// from scratch.</summary>
    public required string TaggedName { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One row per set, always — era/series, release date, and set
/// code, joined from the existing TCGdex mapping (ADR-0009) where set names
/// match; Pending elsewhere (ADR-0011).</summary>
public class SetDetail
{
    public long SetId { get; set; }

    public SetMatchStatus MatchStatus { get; set; }

    /// <summary>TCGdex set id verbatim ("swsh7") — display formatting is
    /// CardStock's job.</summary>
    public string? Code { get; set; }

    public DateOnly? ReleasedOn { get; set; }

    public string? Series { get; set; }

    /// <summary>An era code from the curated series→era file
    /// (tcgdex-series-eras.json), or null.</summary>
    public string? Era { get; set; }
}
