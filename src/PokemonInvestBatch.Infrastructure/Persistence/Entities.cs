using PokemonInvestBatch.Domain.Parsing;

namespace PokemonInvestBatch.Infrastructure.Persistence;

/// <summary>A card set discovered on the category page. Slug is the blacklist key.</summary>
public class CardSet
{
    public long Id { get; set; }

    public required string Slug { get; set; }

    public required string Name { get; set; }

    public DateTimeOffset DiscoveredAt { get; set; }

    /// <summary>Last time the category page listed this set.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Last time this set's card pages were walked to completion.
    /// Null = never walked — an interrupted enumeration resumes here instead
    /// of sleeping out the weekly interval.</summary>
    public DateTimeOffset? LastWalkedAt { get; set; }
}

/// <summary>
/// A card. The primary key is PriceCharting's own product id (e.g. 630417) —
/// stable, and it keeps every fact join trivially consistent with the source.
/// </summary>
public class Card
{
    public long Id { get; set; }

    public long SetId { get; set; }

    public CardSet? Set { get; set; }

    /// <summary>Detail page path, e.g. "/game/pokemon-base-set/charizard-4".</summary>
    public required string Url { get; set; }

    public required string Name { get; set; }

    /// <summary>CDN hash segment of the product image; doubles as its content address.</summary>
    public string? ImageHash { get; set; }

    public DateTimeOffset? ImageFetchedAt { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    // Scheduler state — queue lives in Postgres so restarts are free.
    public DateTimeOffset? LastVisitedAt { get; set; }

    /// <summary>Sales/day observed across buckets at the last visit.</summary>
    public double? ObservedSalesPerDay { get; set; }

    /// <summary>A grade bucket came back full with its oldest row newer than our
    /// previous visit — proof sales were missed; hard override for the queue.</summary>
    public bool AnyBucketAtCap { get; set; }

    /// <summary>Consecutive card-attributable failures (parse drift, 4xx).
    /// Site trouble never counts. Reset by any successful visit.</summary>
    public int FailureStreak { get; set; }

    /// <summary>While set and in the future, the scheduler skips this card —
    /// a poisoned page must not wedge the crawl. See QuarantinePolicy.</summary>
    public DateTimeOffset? QuarantinedUntil { get; set; }

    /// <summary>Set by hand when the product is gone from the site outright —
    /// page and search both empty, so no set walk can ever heal the URL.
    /// The application never writes this column, it only honors it: a
    /// delisted card is invisible to scheduling, the bench recheck, the
    /// image sweep, and the neglect/at-risk alarms, while its history rows
    /// stay put. Clear it by hand if the card comes back — the catalog
    /// cannot tell you when that happens, since it also lists phantom
    /// products whose pages never existed at all. The delisted probe is
    /// what can: it fetches the page itself and shouts on a 200.</summary>
    public DateTimeOffset? DelistedAt { get; set; }

    /// <summary>Last time the delisted probe asked whether this retired card's
    /// page came back. Null = never asked, so it goes first.</summary>
    public DateTimeOffset? DelistedProbedAt { get; set; }
}

/// <summary>
/// One observation of one tier-month of the chart. Change-only append:
/// a row is written only when the value differs from the last observation,
/// so closed months carry exactly one row and nothing is ever overwritten.
/// </summary>
public class CardPriceMonth
{
    public long CardId { get; set; }

    public PriceTier Tier { get; set; }

    public DateOnly Month { get; set; }

    public int PriceCents { get; set; }

    public DateTimeOffset ObservedAt { get; set; }
}

/// <summary>
/// One observation of one grader/grade census cell. Change-only append;
/// deltas come from LAG() over ObservedAt.
/// </summary>
public class CardPopulation
{
    public long CardId { get; set; }

    /// <summary>"psa" or "cgc" — the parser rejects anything else as drift.</summary>
    public required string Grader { get; set; }

    /// <summary>1..10.</summary>
    public short Grade { get; set; }

    public int Population { get; set; }

    public DateTimeOffset ObservedAt { get; set; }
}

/// <summary>An immutable completed sale. UNIQUE (Source, SourceId) is the dedup guarantee.</summary>
public class Sale
{
    public long Id { get; set; }

    public long CardId { get; set; }

    public required string Source { get; set; }

    public required string SourceId { get; set; }

    public DateOnly SoldOn { get; set; }

    /// <summary>Grade bucket label exactly as the page named it.</summary>
    public required string GradeTier { get; set; }

    public int PriceCents { get; set; }

    public int? ListedPriceCents { get; set; }

    /// <summary>Raw third-party listing title. Stored raw; encoded on output.</summary>
    public required string Title { get; set; }

    public DateTimeOffset CapturedAt { get; set; }
}

public enum PageKind : short
{
    CardDetail,
    Console,
    Category,
}

public enum VisitOutcome : short
{
    Parsed,
    ParseFailed,
    HttpError,
}

/// <summary>
/// One row per fetch — distinguishes "we looked and nothing changed"
/// from "we never looked", which change-only storage alone cannot.
/// </summary>
public class PageVisit
{
    public long Id { get; set; }

    public PageKind Kind { get; set; }

    public required string Url { get; set; }

    public long? CardId { get; set; }

    public DateTimeOffset FetchedAt { get; set; }

    public int HttpStatus { get; set; }

    public VisitOutcome Outcome { get; set; }

    public string? ShapeHash { get; set; }
}

/// <summary>A structural fingerprint we have seen. A hash not in this table is an alert.</summary>
public class PageShape
{
    public required string Hash { get; set; }

    public required string ShapeJson { get; set; }

    public required string SampleUrl { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>A page we refused to write facts from, and why.</summary>
public class ParseFailure
{
    public long Id { get; set; }

    public required string Url { get; set; }

    public DateTimeOffset FetchedAt { get; set; }

    public required string Reason { get; set; }

    public string? ShapeHash { get; set; }
}
