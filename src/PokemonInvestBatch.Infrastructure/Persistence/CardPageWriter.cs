using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Application.Scheduling;
using PokemonInvestBatch.Application.Telemetry;
using PokemonInvestBatch.Domain.Parsing;

namespace PokemonInvestBatch.Infrastructure.Persistence;

/// <summary>What a visit actually changed, for the caller to narrate.</summary>
public sealed record CardPageWriteResult
{
    public required int NewPriceRows { get; init; }

    public required int NewPopulationCells { get; init; }

    public required int NewSales { get; init; }

    public required SalesObservation Observation { get; init; }

    /// <summary>The at-cap flag went false to true on this visit — the moment
    /// missed sales were proven, and the only moment worth alerting on. The
    /// fast-track revisits that follow re-observe the same hot buckets and
    /// must not re-raise.</summary>
    public required bool NewlyAtCap { get; init; }

    /// <summary>The census as it stood before this visit, so a restatement can
    /// be recognised after the fact. Handing it back rather than checking here
    /// keeps the alarm out of the write path: nothing is announced about
    /// numbers that failed to commit.</summary>
    public required IReadOnlyDictionary<(string Grader, short Grade), int> PreviousPopulations { get; init; }
}

/// <summary>
/// Committing one parsed card page.
///
/// Everything lands in a single transaction, so a visit is all-or-nothing: a
/// half-written visit would leave prices from the new page beside sales from
/// the old one, and nothing downstream could tell. The known cost of that
/// choice is that a failure on any one field discards the whole visit's work —
/// it has happened, when an over-long image hash rejected by the database
/// threw away prices, populations and sales that had parsed perfectly well.
/// Now that this lives in one place, that is a fixable problem rather than
/// surgery on the crawl lane.
/// </summary>
public static class CardPageWriter
{
    public static async Task<CardPageWriteResult> WriteAsync(
        PokemonDbContext db,
        Card card,
        CardDetailPage page,
        string fingerprintHash,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // Phase span: the visit's only O(card history) section, so it gets its
        // own band in the where-time-goes stack to watch it grow.
        Dictionary<(PriceTier Tier, DateOnly Month), int> lastPrices;
        Dictionary<(string Grader, short Grade), int> lastPops;
        Dictionary<string, int> salesHeldBefore;
        using (CrawlTracing.Source.StartActivity("card.load_history"))
        {
            var priceRows = await db.PriceMonths.AsNoTracking()
                .Where(p => p.CardId == card.Id).ToListAsync(ct);
            lastPrices = LastObserved.ByKey(
                priceRows, p => (p.Tier, p.Month), p => p.ObservedAt, p => p.PriceCents);

            var popRows = await db.Populations.AsNoTracking()
                .Where(p => p.CardId == card.Id).ToListAsync(ct);
            lastPops = LastObserved.ByKey(
                popRows, p => (p.Grader, p.Grade), p => p.ObservedAt, p => p.Population);

            // Counted, not loaded: the at-cap verdict only needs how many rows
            // each bucket held, and a hot card's sale history is the largest
            // thing attached to it.
            salesHeldBefore = await SalesByTierAsync(db, card.Id, ct);
        }

        var newPrices = ChangeOnlyPlanner.NewPricePoints(card.Id, page.Chart, lastPrices, now);
        var newPops = page.Population is null
            ? []
            : ChangeOnlyPlanner.NewPopulationCells(card.Id, page.Population, lastPops, now);

        int newSales;
        SalesObservation observation;
        bool newlyAtCap;
        using (CrawlTracing.Source.StartActivity("card.write"))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            db.PriceMonths.AddRange(newPrices);
            db.Populations.AddRange(newPops);
            newSales = await new SaleWriter(db).AppendNewAsync(card.Id, page.Sales, now, ct);

            // The verdict has to come after the append, because what the append
            // *collided* with is the evidence: a bucket whose page shared no row
            // with our records rolled past us. Same transaction, so this reads
            // the rows just written and nothing else's.
            var overlap = SalesOverlap.Between(
                salesHeldBefore, await SalesByTierAsync(db, card.Id, ct));
            observation = SalesObservation.From(page.Sales, overlap, now);
            newlyAtCap = observation.AnyBucketAtCap && !card.AnyBucketAtCap;

            db.Visits.Add(new PageVisit
            {
                Kind = PageKind.CardDetail,
                Url = card.Url,
                CardId = card.Id,
                FetchedAt = now,
                HttpStatus = 200,
                Outcome = VisitOutcome.Parsed,
                FingerprintHash = fingerprintHash,
            });

            card.LastVisitedAt = now;
            card.LastSeenAt = now;
            card.ObservedSalesPerDay = observation.SalesPerDay;
            card.AnyBucketAtCap = observation.AnyBucketAtCap;
            card.ImageHash ??= page.ImageHash;
            card.FailureStreak = 0;
            card.QuarantinedUntil = null;
            // Any successful visit satisfies a pending refresh ask, whichever
            // path delivered it — the lane's turn or an express visit.
            card.RefreshRequestedAt = null;

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }

        return new CardPageWriteResult
        {
            NewPriceRows = newPrices.Count,
            NewPopulationCells = newPops.Count,
            NewSales = newSales,
            Observation = observation,
            NewlyAtCap = newlyAtCap,
            PreviousPopulations = lastPops,
        };
    }

    /// <summary>How many sale rows this card holds in each grade bucket.</summary>
    private static async Task<Dictionary<string, int>> SalesByTierAsync(
        PokemonDbContext db, long cardId, CancellationToken ct) =>
        await db.Sales.AsNoTracking()
            .Where(s => s.CardId == cardId)
            .GroupBy(s => s.GradeTier)
            .Select(g => new { Tier = g.Key, Rows = g.Count() })
            .ToDictionaryAsync(x => x.Tier, x => x.Rows, ct);
}
