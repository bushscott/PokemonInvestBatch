using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PokemonInvestBatch.Application.Alerting;
using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Application.Scheduling;
using PokemonInvestBatch.Application.Telemetry;
using PokemonInvestBatch.Infrastructure.Http;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.TestSupport;
using PokemonInvestBatch.Worker.Intake;
using PokemonInvestBatch.Worker.Lanes;

namespace PokemonInvestBatch.Worker.Tests;

/// <summary>
/// Answers requests from a script, so a test can stage a run of failures and
/// then a recovery. The last entry repeats for every request after it.
///
/// Entries are factories, not responses. HttpClient disposes a response's
/// content once it has been read, so handing out the same instance twice fails
/// on the second read with ObjectDisposedException — which looks exactly like
/// a bug in the code under test until you read the stack trace.
/// </summary>
public sealed class ScriptedHandler(params Func<HttpResponseMessage>[] responses) : HttpMessageHandler
{
    private int _index;

    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(responses[Math.Min(_index++, responses.Length - 1)]());
    }

    public static Func<HttpResponseMessage> Redirect(string to) => () =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return response;
    };

    public static Func<HttpResponseMessage> Page(string html) => () =>
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
public sealed class LaneHarness(DbContextOptions<PokemonDbContext> options, string fingerprintDirectory) : IDisposable
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

    public CardVisitor Visitor { get; private set; } = null!;

    /// <summary>A real SetWalker over a scripted site — the cataloging errand
    /// without the lane's schedule around it.</summary>
    public SetWalker BuildWalker(ScriptedHandler handler, ScraperOptions? scraper = null)
    {
        Metrics = new CrawlMetrics(Delay);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.pricecharting.com") };
        var client = new PriceChartingClient(http, "tests@example.com", TimeProvider.System);
        return new SetWalker(
            new Factory(options),
            client,
            new PoliteGate(Delay, TimeProvider.System),
            Delay,
            new IncidentThrottle(TimeSpan.FromHours(6)),
            Alerter,
            TimeProvider.System,
            Options.Create(scraper ?? new ScraperOptions { ContactEmail = "tests@example.com" }),
            Metrics,
            NullLogger<SetWalker>.Instance);
    }

    public DetailCrawlLane Build(ScriptedHandler handler, IncidentThrottle? alertThrottle = null)
    {
        Metrics = new CrawlMetrics(Delay);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.pricecharting.com") };
        var client = new PriceChartingClient(http, "tests@example.com", TimeProvider.System);
        // A test asserting alert CADENCE passes a zero-window throttle, so the
        // suppression it observes is the code's own and not the 6h window's.
        var throttle = alertThrottle ?? new IncidentThrottle(TimeSpan.FromHours(6));
        var scraperOptions = Options.Create(new ScraperOptions
        {
            ContactEmail = "tests@example.com",
            FingerprintArchiveDirectory = fingerprintDirectory,
            // A tripped pause must not park the test for half an hour.
            PauseCooldownMinutes = 0,
        });

        Visitor = new CardVisitor(
            client,
            Delay,
            throttle,
            Alerter,
            new PageFingerprintArchive(throttle, Alerter, fingerprintDirectory),
            NewResolver(client, throttle, scraperOptions),
            TimeProvider.System,
            scraperOptions,
            Metrics,
            NullLogger<CardVisitor>.Instance);

        return new DetailCrawlLane(
            new Factory(options),
            Visitor,
            new PoliteGate(Delay, TimeProvider.System),
            Delay,
            throttle,
            Alerter,
            TimeProvider.System,
            scraperOptions,
            Options.Create(new VisitPriorityOptions()),
            Metrics,
            NullLogger<DetailCrawlLane>.Instance);
    }

    /// <summary>The express path over the same scripted site. Any
    /// HttpMessageHandler is accepted so a test can gate a response open
    /// while a second caller coalesces onto the in-flight visit.</summary>
    public ExpressVisitRunner BuildExpressRunner(
        HttpMessageHandler handler,
        TimeProvider? clock = null,
        ScraperOptions? scraper = null)
    {
        clock ??= TimeProvider.System;
        Metrics = new CrawlMetrics(Delay);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.pricecharting.com") };
        var client = new PriceChartingClient(http, "tests@example.com", clock);
        var throttle = new IncidentThrottle(TimeSpan.FromHours(6));
        var scraperOptions = Options.Create(scraper ?? new ScraperOptions
        {
            ContactEmail = "tests@example.com",
            FingerprintArchiveDirectory = fingerprintDirectory,
            PauseCooldownMinutes = 0,
        });

        Visitor = new CardVisitor(
            client,
            Delay,
            throttle,
            Alerter,
            new PageFingerprintArchive(throttle, Alerter, fingerprintDirectory),
            NewResolver(client, throttle, scraperOptions),
            clock,
            scraperOptions,
            Metrics,
            NullLogger<CardVisitor>.Instance);

        return new ExpressVisitRunner(
            new Factory(options),
            Visitor,
            new PoliteGate(Delay, clock),
            clock,
            Metrics,
            applicationStopping: CancellationToken.None,
            NullLogger<ExpressVisitRunner>.Instance);
    }

    public void Dispose() => Metrics?.Dispose();

    /// <summary>The verdict path's collaborators over the same scripted site:
    /// listing fetches ride the SAME handler as card fetches, exactly as in
    /// production where one PriceChartingClient serves both.</summary>
    private MissingCardResolver NewResolver(
        PriceChartingClient client, IncidentThrottle throttle, IOptions<ScraperOptions> scraperOptions) =>
        new(
            new SetWalker(
                new Factory(options),
                client,
                new PoliteGate(Delay, TimeProvider.System),
                Delay,
                throttle,
                Alerter,
                TimeProvider.System,
                scraperOptions,
                Metrics,
                NullLogger<SetWalker>.Instance),
            throttle,
            Alerter,
            NullLogger<MissingCardResolver>.Instance);

    private sealed class Factory(DbContextOptions<PokemonDbContext> contextOptions)
        : IDbContextFactory<PokemonDbContext>
    {
        public PokemonDbContext CreateDbContext() => new(contextOptions);
    }
}
