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

    [Theory]
    [InlineData("https://evil.example/game/x")]
    [InlineData("//evil.example/game/x")]
    [InlineData("game/relative-without-slash")]
    public async Task Non_site_relative_paths_are_refused_before_any_bytes_leave(string path)
    {
        // BaseAddress is only a default — an absolute URI overrides it. The
        // client is the last line between a stored hostile href and an
        // outbound request wearing our User-Agent.
        var (client, handler) = NewClient();

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.GetAsync(path, CancellationToken.None));

        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task A_body_declared_over_the_cap_is_never_read()
    {
        var content = new StringContent("<html/>");
        content.Headers.ContentLength = PriceChartingClient.MaxBodyBytes + 1;
        var (client, _) = NewClient(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var fetched = await client.GetAsync("/game/pokemon-base-set/charizard-4", CancellationToken.None);

        Assert.Equal(0, fetched.StatusCode);
        Assert.IsType<FetchFailure>(fetched);
    }

    [Fact]
    public async Task A_stream_that_outgrows_the_cap_is_cut_off_mid_body()
    {
        // No Content-Length at all — the chunked-transfer size bomb. The
        // bounded read must bail, not buffer until the Pi runs out of RAM.
        var (client, _) = NewClient(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new EndlessContent(PriceChartingClient.MaxBodyBytes + 81_920),
        });

        var fetched = await client.GetAsync("/game/pokemon-base-set/charizard-4", CancellationToken.None);

        Assert.Equal(0, fetched.StatusCode);
        Assert.IsType<FetchFailure>(fetched);
    }

    private sealed class EndlessContent(long totalBytes) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var chunk = new byte[81_920];
            for (var sent = 0L; sent < totalBytes; sent += chunk.Length)
            {
                await stream.WriteAsync(chunk);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
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
    public async Task A_redirect_is_a_failure_that_names_its_destination()
    {
        // The handler is built with AllowAutoRedirect = false, so a renamed
        // card's 302 arrives here instead of the search page it points at.
        // The destination rides the failure so the log can say "moved where".
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(
            "https://www.pricecharting.com/search-products?type=prices&q=misdreavus");
        var (client, _) = NewClient(response);

        var result = await client.GetAsync(
            "/game/pokemon-japanese-awakening-legends/misdreavus", CancellationToken.None);

        var failure = Assert.IsType<FetchFailure>(result);
        Assert.Equal(302, failure.StatusCode);
        Assert.Contains("/search-products", failure.RedirectTarget);
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
        Assert.IsType<FetchFailure>(result);
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
        Assert.IsType<FetchFailure>(result);
    }

    private sealed class DyingBodyStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("connection reset mid-body");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_body_that_dies_mid_download_is_a_failed_fetch_not_an_exception()
    {
        // Headers said 200, then the connection reset while streaming the
        // body. Same contract as a dead site: status 0, no crash, so the
        // AIMD backoff owns it instead of the lane's catch-all.
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new DyingBodyStream()),
        };
        var http = new HttpClient(new RecordingHandler(response))
        {
            BaseAddress = new Uri("https://www.pricecharting.com"),
        };
        var client = new PriceChartingClient(http, "scbush88@gmail.com", TimeProvider.System);

        var result = await client.GetAsync("/game/x", CancellationToken.None);

        Assert.Equal(0, result.StatusCode);
        Assert.IsType<FetchFailure>(result);
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
        Assert.Equal("<html/>", Assert.IsType<FetchedPage>(result).Html);
    }
}
