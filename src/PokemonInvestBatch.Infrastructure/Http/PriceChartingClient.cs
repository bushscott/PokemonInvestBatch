using System.Diagnostics;
using PokemonInvestBatch.Application.Telemetry;

namespace PokemonInvestBatch.Infrastructure.Http;

/// <summary>One page fetch, as the lanes see it.</summary>
public sealed record FetchResult
{
    public required int StatusCode { get; init; }

    /// <summary>Body on success; null on any non-2xx.</summary>
    public string? Html { get; init; }

    public required TimeSpan Latency { get; init; }

    /// <summary>Server-demanded backoff, when sent.</summary>
    public TimeSpan? RetryAfter { get; init; }
}

/// <summary>
/// The only code that talks to pricecharting.com. Every request carries a
/// User-Agent naming the bot and a contact address, so an unhappy operator
/// can reach a person before reaching for a block.
/// </summary>
public sealed class PriceChartingClient(HttpClient http, string contactEmail, TimeProvider time)
{
    public Task<FetchResult> GetAsync(string path, CancellationToken cancellationToken) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, path), cancellationToken);

    public Task<FetchResult> PostFormAsync(
        string path,
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new FormUrlEncodedContent(form),
        }, cancellationToken);

    private async Task<FetchResult> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.OriginalString ?? string.Empty;
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal))
        {
            // HttpClient lets an absolute (or protocol-relative) URI override
            // BaseAddress — a scraped href could aim the bot at any host.
            // This class claims to be the only code that talks to the site;
            // this is where the claim is enforced. Nothing has been sent yet.
            throw new ArgumentException(
                $"Refusing to fetch '{path[..Math.Min(path.Length, 80)]}': "
                + "only site-relative paths may leave this client.");
        }

        request.Headers.TryAddWithoutValidation(
            "User-Agent", $"PokemonInvestBatch/0.1 (+mailto:{contactEmail})");

        var started = time.GetTimestamp();
        HttpResponseMessage response;

        // Headers and body are timed as separate spans so telemetry can
        // tell "their server is slow to answer" from "the page got huge".
        using (var wait = CrawlTracing.Source.StartActivity("site.wait"))
        {
            // Lets telemetry queries scope fetch timings by page kind.
            wait?.SetTag("url.path", request.RequestUri?.OriginalString);
            try
            {
                response = await http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (Exception e) when (
                e is HttpRequestException
                || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // Connection refused, DNS failure, timeout: status 0 so a dead
                // site trips the same backoff/pause/canary alarms as a 5xx.
                wait?.SetStatus(ActivityStatusCode.Error, e.Message);
                return new FetchResult
                {
                    StatusCode = 0,
                    Latency = time.GetElapsedTime(started),
                };
            }
        }

        using var _ = response;
        if (!response.IsSuccessStatusCode)
        {
            return new FetchResult
            {
                StatusCode = (int)response.StatusCode,
                Latency = time.GetElapsedTime(started),
                RetryAfter = response.Headers.RetryAfter?.Delta,
            };
        }

        string html;
        using (var download = CrawlTracing.Source.StartActivity("site.download"))
        {
            download?.SetTag("url.path", request.RequestUri?.OriginalString);
            try
            {
                html = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (Exception e) when (
                e is HttpRequestException or IOException
                || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // Headers promised a page; the connection died mid-body.
                // Same contract as a dead site: status 0 into the AIMD
                // machinery, never an unhandled crash in a lane.
                download?.SetStatus(ActivityStatusCode.Error, e.Message);
                return new FetchResult
                {
                    StatusCode = 0,
                    Latency = time.GetElapsedTime(started),
                };
            }
        }

        return new FetchResult
        {
            StatusCode = (int)response.StatusCode,
            Html = html,
            Latency = time.GetElapsedTime(started),
            RetryAfter = response.Headers.RetryAfter?.Delta,
        };
    }
}
