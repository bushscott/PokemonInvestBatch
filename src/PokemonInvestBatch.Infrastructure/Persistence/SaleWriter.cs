using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Domain.Parsing;

namespace PokemonInvestBatch.Infrastructure.Persistence;

/// <summary>
/// The one deliberate raw-SQL path in the codebase (everything else is LINQ):
/// a single constant statement inserting a page's sales with dedup at the
/// database. Every value arrives as a typed array parameter via interpolation
/// — the SQL text itself never contains data, so hostile listing titles are
/// inert. ExecuteSqlRaw stays banned.
/// </summary>
public sealed class SaleWriter(PokemonDbContext db)
{
    /// <summary>
    /// Appends the sales of one card page; rows whose (source, source_id)
    /// already exist are skipped by ON CONFLICT. Returns how many were new.
    /// </summary>
    public Task<int> AppendNewAsync(
        long cardId,
        IReadOnlyList<SaleRecord> sales,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        if (sales.Count == 0)
        {
            return Task.FromResult(0);
        }

        var sources = new string[sales.Count];
        var sourceIds = new string[sales.Count];
        var soldOns = new DateOnly[sales.Count];
        var gradeTiers = new string[sales.Count];
        var priceCents = new int[sales.Count];
        var listedPriceCents = new int?[sales.Count];
        var titles = new string[sales.Count];
        for (var i = 0; i < sales.Count; i++)
        {
            var sale = sales[i];
            sources[i] = sale.Source;
            sourceIds[i] = sale.SourceId;
            soldOns[i] = sale.SoldOn;
            gradeTiers[i] = sale.GradeTier;
            priceCents[i] = sale.PriceCents;
            listedPriceCents[i] = sale.ListedPriceCents;
            titles[i] = sale.Title;
        }

        return db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO sales (card_id, source, source_id, sold_on, grade_tier,
                               price_cents, listed_price_cents, title, captured_at)
            SELECT {cardId}, u.source, u.source_id, u.sold_on, u.grade_tier,
                   u.price_cents, u.listed_price_cents, u.title, {capturedAt}
            FROM unnest({sources}, {sourceIds}, {soldOns}, {gradeTiers},
                        {priceCents}, {listedPriceCents}, {titles})
                 AS u(source, source_id, sold_on, grade_tier,
                      price_cents, listed_price_cents, title)
            ON CONFLICT (source, source_id) DO NOTHING
            """,
            cancellationToken);
    }
}
