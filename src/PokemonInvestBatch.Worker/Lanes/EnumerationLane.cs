using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Application.Telemetry;
using PokemonInvestBatch.Domain.Parsing;
using PokemonInvestBatch.Infrastructure.Http;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Worker.Lanes;

/// <summary>
/// Weekly discovery: category page → sets (minus blacklist) → cursor-walked
/// console pages → card URLs. Enumeration only; no prices are read here by
/// design. New sets appear automatically as PriceCharting adds them.
/// </summary>
public sealed class EnumerationLane(
    IDbContextFactory<PokemonDbContext> dbFactory,
    PriceChartingClient client,
    PoliteGate gate,
    AdaptiveDelay delay,
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
                if (await IsDueAsync(stoppingToken))
                {
                    await EnumerateAsync(stoppingToken);
                }
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

    private async Task<bool> IsDueAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var lastSeen = await db.Sets.AsNoTracking()
            .MaxAsync(s => (DateTimeOffset?)s.LastSeenAt, ct);
        return lastSeen is null
            || time.GetUtcNow() - lastSeen >= TimeSpan.FromDays(options.Value.EnumerationIntervalDays);
    }

    private async Task EnumerateAsync(CancellationToken ct)
    {
        var blacklist = Blacklist.Parse(await File.ReadAllTextAsync(options.Value.BlacklistPath, ct));

        await gate.WaitTurnAsync(ct);
        var category = await client.GetAsync(options.Value.CategoryPath, ct);
        metrics.RecordRequest("enumeration", category.StatusCode);
        if (category.Html is null)
        {
            delay.RecordFailure(category.RetryAfter);
            logger.LogWarning("Category page fetch failed with {Status}", category.StatusCode);
            return;
        }

        delay.RecordSuccess(category.Latency);
        var sets = CategoryPageParser.ParseSets(category.Html);
        var wanted = sets.Where(s => !blacklist.Contains(s.Slug)).ToList();
        logger.LogInformation(
            "Discovered {Total} sets; {Wanted} after blacklist", sets.Count, wanted.Count);

        foreach (var listing in wanted)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await EnumerateSetAsync(listing, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Set {Slug} enumeration failed; next cycle retries it", listing.Slug);
            }
        }
    }

    private async Task EnumerateSetAsync(SetListing listing, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var set = await db.Sets.SingleOrDefaultAsync(s => s.Slug == listing.Slug, ct);
        if (set is null)
        {
            set = new CardSet
            {
                Slug = listing.Slug,
                Name = listing.Name,
                DiscoveredAt = now,
                LastSeenAt = now,
            };
            db.Sets.Add(set);
        }
        else
        {
            set.LastSeenAt = now;
        }

        await db.SaveChangesAsync(ct);

        var path = $"/console/{Uri.EscapeDataString(set.Slug).Replace("%2F", "/")}";
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
            metrics.RecordRequest("enumeration", fetched.StatusCode);
            if (fetched.Html is null)
            {
                delay.RecordFailure(fetched.RetryAfter);
                logger.LogWarning("Set {Slug} page fetch failed with {Status}", set.Slug, fetched.StatusCode);
                return;
            }

            delay.RecordSuccess(fetched.Latency);
            var page = ConsolePageParser.Parse(fetched.Html);
            seen += await UpsertCardsAsync(db, set, page.Products, ct);
            form = page.NextPageForm;
            pages++;
        }
        while (form is not null);

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
