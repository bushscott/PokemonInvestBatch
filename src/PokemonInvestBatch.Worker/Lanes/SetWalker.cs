using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PokemonInvestBatch.Application.Alerting;
using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Application.Telemetry;
using PokemonInvestBatch.Domain.Parsing;
using PokemonInvestBatch.Infrastructure.Http;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Worker.Lanes;

/// <summary>What one walk attempt established. Only a completed cursor walk
/// may be treated as the catalog's testimony — an incomplete walk proves
/// nothing about any card it did not reach.</summary>
public sealed record SetWalkResult
{
    /// <summary>The cursor ran to its natural end and LastWalkedAt was
    /// stamped. False = a page failed, the walk was abandoned, or the
    /// pagination ran away; the next hourly cycle retries.</summary>
    public required bool Completed { get; init; }

    /// <summary>Products the listing pages actually carried. A completed walk
    /// of zero products is treated as testimony by no one — an empty listing
    /// is far more likely a site change than a set truly emptying.</summary>
    public required int CardsSeen { get; init; }
}

/// <summary>
/// One set's cataloging walk — paging through its console listing 150 cards
/// at a time and healing every known card's URL/name/set by product id. The
/// errand behind two callers, on the CardVisitor precedent: EnumerationLane
/// runs it on the weekly schedule, and the gone-verdict path runs it on
/// demand when a card starts 302ing — the listing is the ground truth that
/// separates renamed from removed, and it must be one implementation or the
/// two callers' answers drift.
/// </summary>
public sealed class SetWalker(
    IDbContextFactory<PokemonDbContext> dbFactory,
    PriceChartingClient client,
    PoliteGate gate,
    AdaptiveDelay delay,
    IncidentThrottle throttle,
    IAlerter alerter,
    TimeProvider time,
    IOptions<ScraperOptions> options,
    CrawlMetrics metrics,
    ILogger<SetWalker> logger)
{
    public async Task<SetWalkResult> WalkSetAsync(long setId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var set = await db.Sets.SingleAsync(s => s.Id == setId, ct);

        // Slugs are stored verbatim from the site's own hrefs, which are
        // already URL-encoded (champion%27s-path). Encoding again turns %27
        // into %2527 and 404s every set with an apostrophe in its name.
        var path = $"/console/{set.Slug}";
        IReadOnlyDictionary<string, string>? form = null;
        var pages = 0;
        var seen = 0;
        do
        {
            ct.ThrowIfCancellationRequested();
            await gate.WaitTurnAsync(ct);
            var fetched = form is null
                ? await client.GetAsync(path, ct)
                : await client.PostFormAsync(path, form, ct);
            fetched.RecordOutcome(metrics, delay, "set catalog");
            if (fetched is not FetchedPage listing)
            {
                logger.LogWarning("Set {Slug} page fetch failed with {Status}; walk left incomplete", set.Slug, fetched.StatusCode);
                return new SetWalkResult { Completed = false, CardsSeen = seen };
            }

            var page = ConsolePageParser.Parse(listing.Html);
            seen += await UpsertCardsAsync(db, set, page.Products, ct);
            form = page.NextPageForm;
            pages++;
            if (form is not null && pages >= options.Value.MaxSetWalkPages)
            {
                // Page N+1 with no end in sight means the pagination shape
                // changed — abandon loudly, leave the walk incomplete so the
                // next hourly cycle retries (and re-alerts if still broken).
                logger.LogError(
                    "Set {Slug} still offers a next page after {Pages} pages — abandoning walk",
                    set.Slug, pages);
                if (throttle.ShouldAlert("set-walk-runaway", time.GetUtcNow()))
                {
                    await alerter.RaiseAsync(
                        $"Set walk runaway: {set.Slug}",
                        $"After {pages} pages ({seen} cards) the set still offers a next-page form. "
                        + "The biggest real set fits in 4 pages of 150, so the pagination shape has "
                        + "likely changed. The walk was abandoned and will retry next cycle.",
                        ct);
                }

                return new SetWalkResult { Completed = false, CardsSeen = seen };
            }
        }
        while (form is not null);

        // Only a completed cursor walk counts.
        set.LastWalkedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Set {Slug}: {Cards} cards over {Pages} pages", set.Slug, seen, pages);
        return new SetWalkResult { Completed = true, CardsSeen = seen };
    }

    private async Task<int> UpsertCardsAsync(
        PokemonDbContext db, CardSet set, IReadOnlyList<ProductListing> products, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var ids = products.Select(p => p.ProductId).ToList();
        var existing = await db.Cards
            .Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);

        foreach (var product in products)
        {
            if (existing.TryGetValue(product.ProductId, out var card))
            {
                if (card.DelistedAt is not null)
                {
                    // Not an alarm, and deliberately not advice: the catalog
                    // lists phantom products whose pages never existed, so a
                    // delisted card appearing here is the normal case and
                    // proves nothing. Only a successful fetch would, and
                    // delisted cards are never fetched. Logged so the
                    // operator — the only one who may reverse the verdict —
                    // still has a trail.
                    logger.LogInformation(
                        "Card {CardId} ({Name}) is delisted but the catalog still lists it at {CardUrl}",
                        card.Id, product.Name, product.Url);
                }

                if (card.SetId != set.Id)
                {
                    // A genuine move is news, not trouble — but it is logged, so
                    // a product listed under two sets flip-flopping weekly would
                    // still leave a visible trail.
                    logger.LogInformation(
                        "Card {CardId} ({Name}) moved from set {OldSetId} to {NewSetSlug} ({NewSetId})",
                        card.Id, product.Name, card.SetId, set.Slug, set.Id);
                    card.SetId = set.Id;
                }

                card.Url = product.Url;
                card.Name = product.Name;
                card.LastSeenAt = now;
            }
            else
            {
                db.Cards.Add(new Card
                {
                    Id = product.ProductId,
                    SetId = set.Id,
                    Url = product.Url,
                    Name = product.Name,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        return products.Count;
    }
}
