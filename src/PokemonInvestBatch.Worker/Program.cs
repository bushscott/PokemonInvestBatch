using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PokemonInvestBatch.Application.Alerting;
using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Infrastructure.Alerting;
using PokemonInvestBatch.Infrastructure.Http;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.Worker;
using PokemonInvestBatch.Worker.Lanes;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<ScraperOptions>()
    .Bind(builder.Configuration.GetSection("Scraper"))
    .Validate(o => !string.IsNullOrWhiteSpace(o.ContactEmail), "Scraper:ContactEmail is required — it goes in the User-Agent.")
    .ValidateOnStart();

var smtp = builder.Configuration.GetSection("Smtp").Get<SmtpOptions>() ?? new SmtpOptions();
builder.Services.AddSingleton(smtp);
if (smtp.IsConfigured)
{
    builder.Services.AddSingleton<IAlerter, SmtpAlerter>();
}
else
{
    builder.Services.AddSingleton<IAlerter, LogOnlyAlerter>();
}

builder.Services.AddDbContextFactory<PokemonDbContext>(options => options
    .UseNpgsql(builder.Configuration.GetConnectionString("Pokemon"))
    .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new AdaptiveDelay(new AdaptiveDelayOptions()));
builder.Services.AddSingleton<PoliteGate>();
builder.Services.AddSingleton(new IncidentThrottle(TimeSpan.FromHours(6)));

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
