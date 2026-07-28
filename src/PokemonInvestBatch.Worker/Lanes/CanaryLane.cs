using Microsoft.Extensions.Options;
using PokemonInvestBatch.Application.Alerting;
using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Domain.Parsing;
using PokemonInvestBatch.Infrastructure.Http;

namespace PokemonInvestBatch.Worker.Lanes;

/// <summary>
/// Detection speed decoupled from crawl speed: a handful of famous, liquid
/// cards fetched every few hours with hard assertions. A site change
/// surfaces here within hours, not at the end of a twelve-day pass.
/// </summary>
public sealed class CanaryLane(
    PriceChartingClient client,
    PoliteGate gate,
    AdaptiveDelay delay,
    IncidentThrottle throttle,
    IAlerter alerter,
    TimeProvider time,
    IOptions<ScraperOptions> options,
    ILogger<CanaryLane> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var path in options.Value.CanaryPaths)
                {
                    await CheckCanaryAsync(path, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Canary sweep failed");
            }

            await Task.Delay(TimeSpan.FromHours(options.Value.CanaryIntervalHours), time, stoppingToken);
        }
    }

    private async Task CheckCanaryAsync(string path, CancellationToken ct)
    {
        await gate.WaitTurnAsync(ct);
        var fetched = await client.GetAsync(path, ct);

        var failures = new List<string>();
        if (fetched.Html is null)
        {
            delay.RecordFailure(fetched.RetryAfter);
            failures.Add($"HTTP {fetched.StatusCode}");
        }
        else
        {
            delay.RecordSuccess(fetched.Latency);
            try
            {
                var page = CardDetailParser.Parse(fetched.Html);
                if (page.Chart.Count < 5)
                {
                    failures.Add($"only {page.Chart.Count} chart tiers");
                }

                if (page.Chart.TryGetValue(PriceTier.Ungraded, out var ungraded) && ungraded.Count < 60)
                {
                    failures.Add($"only {ungraded.Count} months of Ungraded history");
                }

                if (page.Population is null)
                {
                    failures.Add("population report missing");
                }

                if (page.Sales.Select(s => s.GradeTier).Distinct().Count() < 3)
                {
                    failures.Add("fewer than 3 sale buckets");
                }

                failures.AddRange(GradeMonotonicity.Violations(page.Chart)
                    .Select(v => $"monotonicity: {v.Lower} {v.LowerCents}c > {v.Higher} {v.HigherCents}c"));
            }
            catch (SchemaDriftException drift)
            {
                failures.Add($"schema drift: {drift.Message}");
            }
        }

        if (failures.Count == 0)
        {
            logger.LogDebug("Canary {Path} healthy", path);
            return;
        }

        logger.LogError("Canary {Path} FAILED: {Failures}", path, string.Join("; ", failures));
        if (throttle.ShouldAlert($"canary:{path}", time.GetUtcNow()))
        {
            await alerter.RaiseAsync(
                $"Canary failed: {path}",
                $"A known-good card page no longer passes hard assertions.\n\n{string.Join("\n", failures)}\n\n"
                + "The site has likely changed; detail crawling may be writing nothing (or worse).",
                ct);
        }
    }
}
