using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PokemonInvestBatch.Application.Alerting;
using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Application.Telemetry;
using PokemonInvestBatch.Domain.Parsing;
using PokemonInvestBatch.Infrastructure.Http;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Worker.Lanes;

/// <summary>
/// Set discovery and cataloging ("enumeration"): category page → sets
/// (minus blacklist) → console pages paged through 150 cards at a time →
/// card URLs. A "walk" is one full paging pass that catalogs which cards a
/// set contains. Cataloging only; no prices are read here by design.
///
/// Walks are resumable: each set records LastWalkedAt only when its cursor
/// walk completes, so an interrupted cycle picks up the unwalked sets on the
/// next hourly check instead of sleeping out the weekly interval.
/// </summary>
public sealed class EnumerationLane(
    IDbContextFactory<PokemonDbContext> dbFactory,
    PriceChartingClient client,
    PoliteGate gate,
    AdaptiveDelay delay,
    IncidentThrottle throttle,
    IAlerter alerter,
    TimeProvider time,
    IOptions<ScraperOptions> options,
    CrawlMetrics metrics,
    ILogger<EnumerationLane> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Enumeration cycle failed");
            }

            await Task.Delay(TimeSpan.FromHours(1), time, stoppingToken);
        }
    }

    private async Task RunIfDueAsync(CancellationToken ct)
    {
        var blacklist = Blacklist.Parse(await File.ReadAllTextAsync(options.Value.BlacklistPath, ct));

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var pending = await CountPendingAsync(db, blacklist, ct);
            metrics.SetPendingSets(pending);
            if (pending == 0 && await db.Sets.AnyAsync(ct))
            {
                return;
            }
        }

        await DiscoverSetsAsync(ct);
        await WalkPendingSetsAsync(blacklist, ct);
    }

    /// <summary>Sets that are unwalked, or whose walk is older than the interval.</summary>
    private async Task<int> CountPendingAsync(PokemonDbContext db, Blacklist blacklist, CancellationToken ct)
    {
        if (!await db.Sets.AnyAsync(ct))
        {
            return int.MaxValue;
        }

        var cutoff = time.GetUtcNow() - TimeSpan.FromDays(options.Value.EnumerationIntervalDays);
        var candidates = await db.Sets.AsNoTracking()
            .Where(s => s.LastWalkedAt == null || s.LastWalkedAt < cutoff)
            .Select(s => s.Slug)
            .ToListAsync(ct);
        return candidates.Count(slug => !blacklist.Contains(slug));
    }

    /// <summary>Refresh the set catalog from the category page (one request).</summary>
    private async Task DiscoverSetsAsync(CancellationToken ct)
    {
        await gate.WaitTurnAsync(ct);
        var category = await client.GetAsync(options.Value.CategoryPath, ct);
        category.RecordOutcome(metrics, delay, "set catalog");
        if (category is not FetchedPage categoryPage)
        {
            logger.LogWarning("Category page fetch failed with {Status}", category.StatusCode);
            return;
        }

        var listings = CategoryPageParser.ParseSets(categoryPage.Html);
        var now = time.GetUtcNow();

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var known = await db.Sets.ToDictionaryAsync(s => s.Slug, ct);
        foreach (var listing in listings)
        {
            if (known.TryGetValue(listing.Slug, out var set))
            {
                set.Name = listing.Name;
                set.LastSeenAt = now;
            }
            else
            {
                db.Sets.Add(new CardSet
                {
                    Slug = listing.Slug,
                    Name = listing.Name,
                    DiscoveredAt = now,
                    LastSeenAt = now,
                });
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Category lists {Count} sets", listings.Count);
    }

    private async Task WalkPendingSetsAsync(Blacklist blacklist, CancellationToken ct)
    {
        List<long> pendingIds;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var cutoff = time.GetUtcNow() - TimeSpan.FromDays(options.Value.EnumerationIntervalDays);
            // Never-walked sets first, then stalest walks.
            var candidates = await db.Sets.AsNoTracking()
                .Where(s => s.LastWalkedAt == null || s.LastWalkedAt < cutoff)
                .OrderBy(s => s.LastWalkedAt != null)
                .ThenBy(s => s.LastWalkedAt)
                .Select(s => new { s.Id, s.Slug })
                .ToListAsync(ct);
            pendingIds = [.. candidates.Where(s => !blacklist.Contains(s.Slug)).Select(s => s.Id)];
        }

        metrics.SetPendingSets(pendingIds.Count);
        for (var i = 0; i < pendingIds.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await WalkSetAsync(pendingIds[i], ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Set walk failed; the next cycle resumes it");
            }

            metrics.SetPendingSets(pendingIds.Count - i - 1);
        }
    }

    private async Task WalkSetAsync(long setId, CancellationToken ct)
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
                return;
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

                return;
            }
        }
        while (form is not null);

        // Only a completed cursor walk counts.
        set.LastWalkedAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Set {Slug}: {Cards} cards over {Pages} pages", set.Slug, seen, pages);
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
