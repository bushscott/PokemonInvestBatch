using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Application.Telemetry;
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
    CrawlMetrics metrics,
    TimeProvider time,
    IOptions<ScraperOptions> options,
    ILogger<ImageLane> logger) : BackgroundService
{
    public const string HttpClientName = "images";

    private const string CdnBase = "https://storage.googleapis.com/images.pricecharting.com";

    /// <summary>~100x a real 1600.jpg (325x450, tens of KB). The CDN always
    /// declares Content-Length for static files; an undeclared or oversized
    /// body is not an image we want.</summary>
    private const long MaxImageBytes = 5 * 1024 * 1024;

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
        var done = 0;
        var deferred = 0;
        foreach (var card in pending)
        {
            ct.ThrowIfCancellationRequested();
            var directory = Path.Combine(options.Value.ImageDirectory, card.ImageHash!);
            var file = Path.Combine(directory, "1600.jpg");

            if (File.Exists(file))
            {
                card.ImageFetchedAt = time.GetUtcNow();
                done++;
                continue;
            }

            try
            {
                // Headers first — the default GetAsync buffers the entire
                // body into memory before the status code is even seen.
                using var response = await http.GetAsync(
                    $"{CdnBase}/{card.ImageHash}/1600.jpg", HttpCompletionOption.ResponseHeadersRead, ct);

                // Counted directly, never through FetchBookkeeping: CDN
                // results must not steer pricecharting.com's courtesy delay.
                metrics.RecordRequest("images", (int)response.StatusCode);
                var declared = response.Content.Headers.ContentLength;
                if (response.IsSuccessStatusCode && declared is null or > MaxImageBytes)
                {
                    // Fetch-once applies to junk too: never re-download a
                    // body that will never be stored.
                    logger.LogWarning(
                        "Image {Hash} for card {CardId} declares {Declared} bytes (cap {Cap}) — giving up",
                        card.ImageHash, card.Id, declared, MaxImageBytes);
                    card.ImageFetchedAt = time.GetUtcNow();
                    done++;
                }
                else if (response.IsSuccessStatusCode)
                {
                    Directory.CreateDirectory(directory);
                    await File.WriteAllBytesAsync(file, await response.Content.ReadAsByteArrayAsync(ct), ct);
                    card.ImageFetchedAt = time.GetUtcNow();
                    done++;
                }
                else if (ImageRetryPolicy.GiveUp((int)response.StatusCode))
                {
                    // Fetch-once still applies: a 404 is recorded so we never
                    // hammer the CDN for an image that does not exist.
                    logger.LogWarning(
                        "Image {Hash} for card {CardId} does not exist ({Status}) — giving up",
                        card.ImageHash, card.Id, (int)response.StatusCode);
                    card.ImageFetchedAt = time.GetUtcNow();
                    done++;
                }
                else
                {
                    // Transient CDN trouble: leave the card pending for the
                    // next sweep instead of losing the image forever.
                    logger.LogWarning(
                        "Image {Hash} for card {CardId} returned {Status} — will retry next sweep",
                        card.ImageHash, card.Id, (int)response.StatusCode);
                    deferred++;
                }
            }
            catch (HttpRequestException e)
            {
                // Transport death is status 0, same convention as the polite
                // client. One flaky fetch must not abort the sweep and discard
                // the progress marks of the cards before it.
                metrics.RecordRequest("images", 0);
                logger.LogWarning(e, "Image {Hash} for card {CardId} failed transport — will retry next sweep",
                    card.ImageHash, card.Id);
                deferred++;
            }
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Fetched {Done} card images ({Deferred} deferred to next sweep)", done, deferred);
    }
}
