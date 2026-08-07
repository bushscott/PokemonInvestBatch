using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PokemonInvestBatch.Application.Alerting;
using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Application.Telemetry;
using PokemonInvestBatch.Infrastructure.Http;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.Worker.Lanes;

namespace PokemonInvestBatch.Worker.Tests;

/// <summary>Records what a lane tried to tell a human, so tests can assert on
/// the alarm as well as on the data.</summary>
public sealed class RecordingAlerter : IAlerter
{
    public List<(string Subject, string Body)> Raised { get; } = [];

    public Task RaiseAsync(string subject, string body, CancellationToken ct)
    {
        Raised.Add((subject, body));
        return Task.CompletedTask;
    }
}

/// <summary>Answers requests from a script, so a test can stage a run of
/// failures and then a recovery. The last response repeats.</summary>
public sealed class ScriptedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private int _index;

    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(responses[Math.Min(_index++, responses.Length - 1)]);
    }

    public static HttpResponseMessage Redirect(string to)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return response;
    }

    public static HttpResponseMessage Page(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html) };
}

/// <summary>
/// Builds a real DetailCrawlLane over a real database with a scripted site.
///
/// Everything is genuine except the network: the same EF model, the same
/// migrations, the same transaction. The lanes hold concrete collaborators by
/// design (ADR-0003), so the seam is the HTTP handler — the one boundary a
/// test must not cross — rather than an interface per dependency.
/// </summary>
public sealed class LaneHarness(DbContextOptions<PokemonDbContext> options, string shapeDirectory) : IDisposable
{
    public RecordingAlerter Alerter { get; } = new();

    public AdaptiveDelay Delay { get; } = new(new AdaptiveDelayOptions
    {
        // The courtesy delay is load-bearing in production and pure dead time
        // here; the tests that care about it assert on Current directly.
        Floor = TimeSpan.Zero,
        Ceiling = TimeSpan.Zero,
    });

    public CrawlMetrics Metrics { get; private set; } = null!;

    public DetailCrawlLane Build(ScriptedHandler handler)
    {
        Metrics = new CrawlMetrics(Delay);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.pricecharting.com") };
        var client = new PriceChartingClient(http, "tests@example.com", TimeProvider.System);
        var throttle = new IncidentThrottle(TimeSpan.FromHours(6));

        return new DetailCrawlLane(
            new Factory(options),
            client,
            new PoliteGate(Delay, TimeProvider.System),
            Delay,
            throttle,
            Alerter,
            new PageShapeArchive(throttle, Alerter, shapeDirectory),
            TimeProvider.System,
            Options.Create(new ScraperOptions
            {
                ContactEmail = "tests@example.com",
                ShapeArchiveDirectory = shapeDirectory,
                // A tripped pause must not park the test for half an hour.
                PauseCooldownMinutes = 0,
            }),
            Metrics,
            NullLogger<DetailCrawlLane>.Instance);
    }

    public void Dispose() => Metrics?.Dispose();

    private sealed class Factory(DbContextOptions<PokemonDbContext> contextOptions)
        : IDbContextFactory<PokemonDbContext>
    {
        public PokemonDbContext CreateDbContext() => new(contextOptions);
    }
}
