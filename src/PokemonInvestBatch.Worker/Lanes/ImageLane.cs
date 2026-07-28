using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Worker.Lanes;

/// <summary>
/// Fetch-once product images from the Google CDN — a different host, so this
/// lane sits outside the politeness gate. 1600.jpg (325x450) is the largest
/// size that exists; files land at {ImageDirectory}/{hash}/1600.jpg.
/// </summary>
public sealed class ImageLane(
    IDbContextFactory<PokemonDbContext> dbFactory,
    IHttpClientFactory httpFactory,
    TimeProvider time,
    IOptions<ScraperOptions> options,
    ILogger<ImageLane> logger) : BackgroundService
{
    public const string HttpClientName = "images";

    private const string CdnBase = "https://storage.googleapis.com/images.pricecharting.com";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FetchPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Image sweep failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(options.Value.ImageIntervalMinutes), time, stoppingToken);
        }
    }

    private async Task FetchPendingAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pending = await db.Cards
            .Where(c => c.ImageHash != null && c.ImageFetchedAt == null)
            .OrderBy(c => c.Id)
            .Take(50)
            .ToListAsync(ct);
        if (pending.Count == 0)
        {
            return;
        }

        var http = httpFactory.CreateClient(HttpClientName);
        foreach (var card in pending)
        {
            ct.ThrowIfCancellationRequested();
            var directory = Path.Combine(options.Value.ImageDirectory, card.ImageHash!);
            var file = Path.Combine(directory, "1600.jpg");

            if (!File.Exists(file))
            {
                using var response = await http.GetAsync($"{CdnBase}/{card.ImageHash}/1600.jpg", ct);
                if (response.IsSuccessStatusCode)
                {
                    Directory.CreateDirectory(directory);
                    await File.WriteAllBytesAsync(file, await response.Content.ReadAsByteArrayAsync(ct), ct);
                }
                else
                {
                    // Fetch-once still applies: a 404 is recorded so we never
                    // hammer the CDN for an image that does not exist.
                    logger.LogWarning(
                        "Image {Hash} for card {CardId} returned {Status}",
                        card.ImageHash, card.Id, (int)response.StatusCode);
                }
            }

            card.ImageFetchedAt = time.GetUtcNow();
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Fetched {Count} card images", pending.Count);
    }
}
