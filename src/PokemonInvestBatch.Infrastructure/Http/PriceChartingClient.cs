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
        request.Headers.TryAddWithoutValidation(
            "User-Agent", $"PokemonInvestBatch/0.1 (+mailto:{contactEmail})");

        var started = time.GetTimestamp();
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception e) when (
            e is HttpRequestException
            || (e is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Connection refused, DNS failure, timeout: status 0 so a dead
            // site trips the same backoff/pause/canary alarms as a 5xx.
            return new FetchResult
            {
                StatusCode = 0,
                Latency = time.GetElapsedTime(started),
            };
        }

        using var _ = response;
        var latency = time.GetElapsedTime(started);

        return new FetchResult
        {
            StatusCode = (int)response.StatusCode,
            Html = response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(cancellationToken)
                : null,
            Latency = latency,
            RetryAfter = response.Headers.RetryAfter?.Delta,
        };
    }
}
