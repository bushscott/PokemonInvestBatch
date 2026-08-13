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
    SetWalker walker,
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
                await walker.WalkSetAsync(pendingIds[i], ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Set walk failed; the next cycle resumes it");
            }

            metrics.SetPendingSets(pendingIds.Count - i - 1);
        }
    }
}
