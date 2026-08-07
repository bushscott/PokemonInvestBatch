using Microsoft.Extensions.Options;
using PokemonInvestBatch.Application.Alerting;
using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Application.Telemetry;
using PokemonInvestBatch.Domain.Parsing;
using PokemonInvestBatch.Infrastructure.Http;

namespace PokemonInvestBatch.Worker.Lanes;

/// <summary>
/// A canary — the miner's bird that keels over first — is a spot check on a
/// handful of famous, liquid cards fetched every few hours with hard
/// assertions, so a site change surfaces within hours instead of at the end
/// of a twelve-day pass. Detection speed decoupled from crawl speed.
/// </summary>
public sealed class CanaryLane(
    PriceChartingClient client,
    PoliteGate gate,
    AdaptiveDelay delay,
    IncidentThrottle throttle,
    IAlerter alerter,
    TimeProvider time,
    IOptions<ScraperOptions> options,
    CrawlMetrics metrics,
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
        using var check = CrawlTracing.Source.StartActivity("canary.check");
        check?.SetTag("canary.path", path);

        await gate.WaitTurnAsync(ct);
        var fetched = await client.GetAsync(path, ct);
        fetched.RecordOutcome(metrics, delay, "spot check");

        var failures = new List<string>();
        if (fetched is not FetchedPage fetchedPage)
        {
            failures.Add($"HTTP {fetched.StatusCode}");
        }
        else
        {
            try
            {
                var page = CardDetailParser.Parse(fetchedPage.Html);

                // Named, not counted. A canary is a famous, liquid card and
                // carries every tier the site publishes; "at least five of
                // six" let one tier go missing in silence, which is the one
                // way the site can change that the page-shape vocabulary
                // cannot see — a name that stops appearing introduces no new
                // name to notice.
                var missing = Enum.GetValues<PriceTier>().Where(t => !page.Chart.ContainsKey(t)).ToArray();
                if (missing.Length > 0)
                {
                    failures.Add($"chart tiers missing: {string.Join(", ", missing)}");
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

        metrics.RecordCanaryFailure(path);
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
