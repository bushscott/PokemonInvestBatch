using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using PokemonInvestBatch.Application.Alerting;
using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Application.Telemetry;
using PokemonInvestBatch.Infrastructure.Http;
using PokemonInvestBatch.Infrastructure.Persistence;
using PokemonInvestBatch.Worker;
using PokemonInvestBatch.Worker.Lanes;
using Respawn;

namespace PokemonInvestBatch.Worker.Tests;

/// <summary>Records what a lane tried to tell a human, so tests can assert on
/// the alarm as well as the data.</summary>
public sealed class RecordingAlerter : IAlerter
{
    public List<(string Subject, string Body)> Raised { get; } = [];

    public Task RaiseAsync(string subject, string body, CancellationToken ct)
    {
        Raised.Add((subject, body));
        return Task.CompletedTask;
    }
}

/// <summary>Answers every request with a queued response, so a test can script
/// a run of failures followed by a recovery.</summary>
public sealed class ScriptedHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
{
    private int _index;

    public int Calls { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        // The last scripted response repeats, so "always 302" needs one entry.
        var response = responses[Math.Min(_index++, responses.Length - 1)];
        return Task.FromResult(response);
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
/// Builds a real DetailCrawlLane over the real database with a scripted site.
///
/// Everything here is genuine except the network: the same EF model, the same
/// migrations, the same transaction. The lanes hold concrete collaborators by
/// design (ADR-0003), so the seam is the HTTP handler — the one boundary that
/// must not be crossed in a test — rather than an interface per dependency.
/// </summary>
public sealed class LaneHarness : IAsyncDisposable
{
    public static string? ConnectionString => Environment.GetEnvironmentVariable("POKEMON_TEST_DB");

    private readonly string _shapeDirectory =
        Path.Combine(Path.GetTempPath(), $"shapes-{Guid.NewGuid():N}");

    public RecordingAlerter Alerter { get; } = new();

    public AdaptiveDelay Delay { get; } = new(new AdaptiveDelayOptions
    {
        // The polite wait is real in production and pure dead time here.
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
        var options = Options.Create(new ScraperOptions
        {
            ContactEmail = "tests@example.com",
            ShapeArchiveDirectory = _shapeDirectory,
            // A tripped pause must not park the test for half an hour.
            PauseCooldownMinutes = 0,
        });

        return new DetailCrawlLane(
            new Factory(DbOptions()),
            client,
            new PoliteGate(Delay, TimeProvider.System),
            Delay,
            throttle,
            Alerter,
            new PageShapeArchive(throttle, Alerter, _shapeDirectory),
            TimeProvider.System,
            options,
            Metrics,
            NullLogger<DetailCrawlLane>.Instance);
    }

    public PokemonDbContext NewContext() => new(DbOptions());

    /// <summary>Migrates and empties the database, then seeds one set so cards
    /// have somewhere to belong.</summary>
    public async Task ResetAsync()
    {
        await using var db = NewContext();
        await db.Database.MigrateAsync();

        await using var connection = new NpgsqlConnection(ConnectionString!);
        await connection.OpenAsync();
        var respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            // Erasing applied-migration bookkeeping would make the next
            // MigrateAsync re-run InitialCreate against existing tables.
            TablesToIgnore = [new Respawn.Graph.Table("__EFMigrationsHistory")],
        });
        await respawner.ResetAsync(connection);
    }

    private static DbContextOptions<PokemonDbContext> DbOptions() =>
        new DbContextOptionsBuilder<PokemonDbContext>()
            .UseNpgsql(ConnectionString!)
            .UseSnakeCaseNamingConvention()
            .Options;

    private sealed class Factory(DbContextOptions<PokemonDbContext> options)
        : IDbContextFactory<PokemonDbContext>
    {
        public PokemonDbContext CreateDbContext() => new(options);
    }

    public ValueTask DisposeAsync()
    {
        Metrics?.Dispose();
        if (Directory.Exists(_shapeDirectory))
        {
            Directory.Delete(_shapeDirectory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
