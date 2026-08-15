using System.Diagnostics;
using System.Text;
using PokemonInvestBatch.Application.Telemetry;

namespace PokemonInvestBatch.Infrastructure.Http;

/// <summary>One page fetch, as the lanes see it: a page or a failure,
/// never a maybe. Guard with <c>is not FetchedPage</c> and exit early.</summary>
public abstract record FetchResult
{
    public required int StatusCode { get; init; }

    public required TimeSpan Latency { get; init; }

    /// <summary>Server-demanded backoff, when sent.</summary>
    public TimeSpan? RetryAfter { get; init; }
}

/// <summary>A 2xx and its body — Html is always present here.</summary>
public sealed record FetchedPage : FetchResult
{
    public required string Html { get; init; }
}

/// <summary>Non-2xx, transport death, or a size bomb (status 0). Carries no
/// body on purpose: failure is a type, not a null threaded through the
/// happy path.</summary>
public sealed record FetchFailure : FetchResult
{
    /// <summary>Where a 3xx pointed, so the log answers "moved where?"
    /// without a hand-run curl.</summary>
    public string? RedirectTarget { get; init; }
}

/// <summary>
/// The only code that talks to pricecharting.com. Every request carries a
/// User-Agent naming the bot and a contact address, so an unhappy operator
/// can reach a person before reaching for a block.
/// </summary>
public sealed class PriceChartingClient(HttpClient http, string contactEmail, TimeProvider time)
{
    /// <summary>Ten times the heaviest real card page (~1 MB, 410 sales).
    /// MaxResponseContentBufferSize does not apply after ResponseHeadersRead,
    /// so this cap is the only thing between a size bomb and the Pi's RAM.</summary>
    public const long MaxBodyBytes = 10 * 1024 * 1024;

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
        // This method owns the request from here: HttpClient never disposes the
        // one it is handed, and PostFormAsync's FormUrlEncodedContent holds a
        // buffer until someone does. Taken before the guard below so a refused
        // path releases it too.
        using var owned = request;

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
                return new FetchFailure
                {
                    StatusCode = 0,
                    Latency = time.GetElapsedTime(started),
                };
            }
        }

        using var _ = response;
        if (!response.IsSuccessStatusCode)
        {
            return new FetchFailure
            {
                StatusCode = (int)response.StatusCode,
                Latency = time.GetElapsedTime(started),
                RetryAfter = response.Headers.RetryAfter?.Delta,
                RedirectTarget = response.Headers.Location?.ToString(),
            };
        }

        string html;
        using (var download = CrawlTracing.Source.StartActivity("site.download"))
        {
            download?.SetTag("url.path", request.RequestUri?.OriginalString);
            var declared = response.Content.Headers.ContentLength;
            if (declared > MaxBodyBytes)
            {
                // A size bomb is the site misbehaving — same contract as a
                // dead connection: status 0 into the AIMD machinery.
                download?.SetStatus(ActivityStatusCode.Error,
                    $"Declared body of {declared} bytes exceeds the {MaxBodyBytes}-byte cap.");
                return new FetchFailure
                {
                    StatusCode = 0,
                    Latency = time.GetElapsedTime(started),
                };
            }

            try
            {
                var read = await TryReadBoundedAsync(response.Content, cancellationToken);
                if (read is null)
                {
                    download?.SetStatus(ActivityStatusCode.Error,
                        $"Body exceeded the {MaxBodyBytes}-byte cap mid-stream.");
                    return new FetchFailure
                    {
                        StatusCode = 0,
                        Latency = time.GetElapsedTime(started),
                    };
                }

                html = read;
            }
            catch (Exception e) when (
                e is HttpRequestException or IOException
                || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                // Headers promised a page; the connection died mid-body.
                // Same contract as a dead site: status 0 into the AIMD
                // machinery, never an unhandled crash in a lane.
                download?.SetStatus(ActivityStatusCode.Error, e.Message);
                return new FetchFailure
                {
                    StatusCode = 0,
                    Latency = time.GetElapsedTime(started),
                };
            }
        }

        return new FetchedPage
        {
            StatusCode = (int)response.StatusCode,
            Html = html,
            Latency = time.GetElapsedTime(started),
            RetryAfter = response.Headers.RetryAfter?.Delta,
        };
    }

    /// <summary>Reads at most MaxBodyBytes; null when the stream keeps going —
    /// the guard for servers that omit Content-Length or lie in it.</summary>
    private static async Task<string?> TryReadBoundedAsync(HttpContent content, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffered = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffered.Write(chunk, 0, read);
            if (buffered.Length > MaxBodyBytes)
            {
                return null;
            }
        }

        // ReadAsStringAsync honoured the response charset; so does this.
        return ResolveEncoding(content.Headers.ContentType?.CharSet)
            .GetString(buffered.GetBuffer(), 0, (int)buffered.Length);
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        if (charset is null)
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }
}
