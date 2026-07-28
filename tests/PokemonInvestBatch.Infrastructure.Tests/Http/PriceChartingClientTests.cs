using System.Net;
using PokemonInvestBatch.Infrastructure.Http;

namespace PokemonInvestBatch.Infrastructure.Tests.Http;

public class PriceChartingClientTests
{
    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }

    private static (PriceChartingClient Client, RecordingHandler Handler) NewClient(HttpResponseMessage? response = null)
    {
        var handler = new RecordingHandler(response ?? new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html/>"),
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.pricecharting.com") };
        return (new PriceChartingClient(http, "scbush88@gmail.com", TimeProvider.System), handler);
    }

    [Fact]
    public async Task Every_request_identifies_the_bot_and_a_contact_address()
    {
        var (client, handler) = NewClient();

        await client.GetAsync("/game/pokemon-base-set/charizard-4", CancellationToken.None);

        var userAgent = handler.LastRequest!.Headers.UserAgent.ToString();
        Assert.Contains("PokemonInvestBatch", userAgent);
        Assert.Contains("scbush88@gmail.com", userAgent);
    }

    [Fact]
    public async Task Cursor_forms_are_posted_urlencoded()
    {
        var (client, handler) = NewClient();
        var form = new Dictionary<string, string>
        {
            ["cursor"] = "150",
            ["sort"] = "",
            ["when"] = "none",
            ["release-date"] = "2026-07-28",
        };

        await client.PostFormAsync("/console/pokemon-base-set", form, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("cursor=150", handler.LastBody);
        Assert.Contains("release-date=2026-07-28", handler.LastBody);
    }

    [Fact]
    public async Task Retry_after_seconds_are_surfaced()
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.Add("Retry-After", "120");
        var (client, _) = NewClient(response);

        var result = await client.GetAsync("/game/x", CancellationToken.None);

        Assert.Equal(503, result.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(120), result.RetryAfter);
        Assert.Null(result.Html);
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => throw exception;
    }

    [Fact]
    public async Task A_dead_site_is_a_failed_fetch_not_an_exception()
    {
        // Connection refused/DNS/timeouts must flow through the same AIMD,
        // pause, and canary machinery as server errors — a hard-down site
        // must never be quieter than a half-down one.
        var http = new HttpClient(new ThrowingHandler(new HttpRequestException("connection refused")))
        {
            BaseAddress = new Uri("https://www.pricecharting.com"),
        };
        var client = new PriceChartingClient(http, "scbush88@gmail.com", TimeProvider.System);

        var result = await client.GetAsync("/game/x", CancellationToken.None);

        Assert.Equal(0, result.StatusCode);
        Assert.Null(result.Html);
    }

    [Fact]
    public async Task Cancellation_still_propagates()
    {
        var http = new HttpClient(new ThrowingHandler(new OperationCanceledException()))
        {
            BaseAddress = new Uri("https://www.pricecharting.com"),
        };
        var client = new PriceChartingClient(http, "scbush88@gmail.com", TimeProvider.System);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("/game/x", cts.Token));
    }

    [Fact]
    public async Task Successful_fetches_return_the_html()
    {
        var (client, _) = NewClient();

        var result = await client.GetAsync("/game/x", CancellationToken.None);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("<html/>", result.Html);
    }
}
