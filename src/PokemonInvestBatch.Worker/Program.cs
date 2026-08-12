using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PokemonInvestBatch.Application.Alerting;
using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Application.Scheduling;
using PokemonInvestBatch.Application.Telemetry;
using PokemonInvestBatch.Infrastructure.Http;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.Worker;
using PokemonInvestBatch.Worker.Intake;
using PokemonInvestBatch.Worker.Lanes;

// WebApplication instead of a plain host for one reason: the intake API — a
// loopback-only Kestrel endpoint inside the same process (ADR-0006). Every
// registration below is unchanged; the lanes run exactly as before.
var builder = WebApplication.CreateBuilder(args);

// Stamp TraceId/SpanId into log scopes so a log line links to its trace in NR.
builder.Logging.Configure(o =>
    o.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);

builder.Services.AddOptions<ScraperOptions>()
    .Bind(builder.Configuration.GetSection("Scraper"))
    .Validate(o => !string.IsNullOrWhiteSpace(o.ContactEmail), "Scraper:ContactEmail is required — it goes in the User-Agent.")
    .Validate(o => o.IntakePort is >= 1 and <= 65535, "Scraper:IntakePort must be 1-65535.")
    .Validate(o => IPAddress.TryParse(o.IntakeAddress, out _), "Scraper:IntakeAddress must be an IP literal.")
    .ValidateOnStart();

// Scheduling knobs share the "Scraper" section (same config file, same keys as
// always) but bind to their owner in Application.Scheduling — one default, one
// rationale, and all four knobs turnable, not the two a hand-written copy in
// DetailCrawlLane used to carry.
builder.Services.AddOptions<VisitPriorityOptions>()
    .Bind(builder.Configuration.GetSection("Scraper"))
    .Validate(o => o.HotBurnWindowSafetyFraction is > 0 and <= 1, "Scraper:HotBurnWindowSafetyFraction must be in (0, 1].")
    .Validate(o => o.BurnWindowSafetyFraction is > 0 and <= 1, "Scraper:BurnWindowSafetyFraction must be in (0, 1].")
    .Validate(o => o.HotRateThreshold > 0, "Scraper:HotRateThreshold must be positive.")
    .Validate(o => o.MaxDaysBetweenVisits >= 1, "Scraper:MaxDaysBetweenVisits must be at least 1.")
    .ValidateOnStart();

// Alert decisions live in New Relic; the app emits Critical logs and metrics.
builder.Services.AddSingleton<IAlerter, CriticalLogAlerter>();

// OTLP export to New Relic; without a license key the meters/spans still
// exist locally and nothing is sent (clean for dev runs).
var newRelicKey = builder.Configuration["NewRelic:LicenseKey"];
if (!string.IsNullOrWhiteSpace(newRelicKey))
{
    var otlp = (Action<OpenTelemetry.Exporter.OtlpExporterOptions>)(o =>
    {
        o.Endpoint = new Uri("https://otlp.nr-data.net:4317");
        o.Headers = $"api-key={newRelicKey}";
    });
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("pokemon-invest-batch"))
        .WithTracing(t => t
            .AddSource(CrawlTracing.SourceName)
            .AddHttpClientInstrumentation()
            .AddNpgsql()
            .AddOtlpExporter(otlp))
        .WithMetrics(m => m
            .AddMeter(CrawlMetrics.MeterName)
            .AddRuntimeInstrumentation()
            .AddOtlpExporter((exporter, reader) =>
            {
                otlp(exporter);
                // New Relic wants delta temporality for counters.
                reader.TemporalityPreference = MetricReaderTemporalityPreference.Delta;
            }));
}

builder.Services.AddDbContextFactory<PokemonDbContext>(options => options
    .UseNpgsql(builder.Configuration.GetConnectionString("Pokemon"))
    .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new AdaptiveDelay(new AdaptiveDelayOptions()));
builder.Services.AddSingleton<PoliteGate>();
builder.Services.AddSingleton(new IncidentThrottle(TimeSpan.FromHours(6)));
builder.Services.AddSingleton<CrawlMetrics>();

builder.Services.AddHttpClient(nameof(PriceChartingClient), (services, http) =>
    {
        var scraper = services.GetRequiredService<IOptions<ScraperOptions>>().Value;
        http.BaseAddress = new Uri(scraper.BaseUrl);
        http.Timeout = TimeSpan.FromSeconds(60);
    })
    // A 3xx is the site saying "this page moved" and the lanes must hear it
    // verbatim: a renamed card's old URL redirects to a search page, which
    // would arrive here as a 200 that parses as schema drift.
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton(services =>
{
    var scraper = services.GetRequiredService<IOptions<ScraperOptions>>().Value;
    var http = services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(PriceChartingClient));
    return new PriceChartingClient(http, scraper.ContactEmail, services.GetRequiredService<TimeProvider>());
});
builder.Services.AddHttpClient(ImageLane.HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton(services =>
{
    var scraper = services.GetRequiredService<IOptions<ScraperOptions>>().Value;
    return new PageFingerprintArchive(
        services.GetRequiredService<IncidentThrottle>(),
        services.GetRequiredService<IAlerter>(),
        scraper.FingerprintArchiveDirectory);
});

// One visit implementation for both paths: the detail lane's turn and the
// intake API's express visits.
builder.Services.AddSingleton<CardVisitor>();
builder.Services.AddSingleton<RefreshRequestIntake>();
builder.Services.AddSingleton(services => new ExpressVisitRunner(
    services.GetRequiredService<IDbContextFactory<PokemonDbContext>>(),
    services.GetRequiredService<CardVisitor>(),
    services.GetRequiredService<PoliteGate>(),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<CrawlMetrics>(),
    // The worker's own lifetime, not any request's: a caller hanging up must
    // never abort a visit a coalesced waiter still shares.
    services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping,
    services.GetRequiredService<ILogger<ExpressVisitRunner>>()));

builder.Services.AddHostedService<EnumerationLane>();
builder.Services.AddHostedService<DetailCrawlLane>();
builder.Services.AddHostedService<CanaryLane>();
builder.Services.AddHostedService<ImageLane>();
builder.Services.AddHostedService<StatsLane>();
builder.Services.AddHostedService<DelistedProbeLane>();

// Loopback-only, port from validated config. The explicit Listen overrides
// ASPNETCORE_URLS/launchSettings (Kestrel logs a benign "overriding" line) —
// the config file is the only truth for where this listens.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    var scraper = kestrel.ApplicationServices.GetRequiredService<IOptions<ScraperOptions>>().Value;
    kestrel.Listen(IPAddress.Parse(scraper.IntakeAddress), scraper.IntakePort);
});

var app = builder.Build();
IntakeApi.Map(app);
app.Run();
