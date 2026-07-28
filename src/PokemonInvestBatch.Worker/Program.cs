using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PokemonInvestBatch.Application.Alerting;
using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Application.Telemetry;
using PokemonInvestBatch.Infrastructure.Http;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.Worker;
using PokemonInvestBatch.Worker.Lanes;

var builder = Host.CreateApplicationBuilder(args);

// Stamp TraceId/SpanId into log scopes so a log line links to its trace in NR.
builder.Logging.Configure(o =>
    o.ActivityTrackingOptions = ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId);

builder.Services.AddOptions<ScraperOptions>()
    .Bind(builder.Configuration.GetSection("Scraper"))
    .Validate(o => !string.IsNullOrWhiteSpace(o.ContactEmail), "Scraper:ContactEmail is required — it goes in the User-Agent.")
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
});
builder.Services.AddSingleton(services =>
{
    var scraper = services.GetRequiredService<IOptions<ScraperOptions>>().Value;
    var http = services.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(PriceChartingClient));
    return new PriceChartingClient(http, scraper.ContactEmail, services.GetRequiredService<TimeProvider>());
});
builder.Services.AddHttpClient(ImageLane.HttpClientName, http => http.Timeout = TimeSpan.FromSeconds(60));

builder.Services.AddHostedService<EnumerationLane>();
builder.Services.AddHostedService<DetailCrawlLane>();
builder.Services.AddHostedService<CanaryLane>();
builder.Services.AddHostedService<ImageLane>();

var host = builder.Build();
host.Run();
