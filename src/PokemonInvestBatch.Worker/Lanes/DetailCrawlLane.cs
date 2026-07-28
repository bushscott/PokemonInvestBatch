using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PokemonInvestBatch.Application.Alerting;
using PokemonInvestBatch.Application.Crawling;
using PokemonInvestBatch.Application.Scheduling;
using PokemonInvestBatch.Application.Telemetry;
using PokemonInvestBatch.Domain.Parsing;
using PokemonInvestBatch.Infrastructure.Http;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Worker.Lanes;

/// <summary>
/// The main lane: picks the highest-priority card, fetches its detail page
/// through the shared polite gate, and writes everything the page contains
/// in one transaction. Nothing is written from a page that failed any check.
/// </summary>
public sealed class DetailCrawlLane(
    IDbContextFactory<PokemonDbContext> dbFactory,
    PriceChartingClient client,
    PoliteGate gate,
    AdaptiveDelay delay,
    IncidentThrottle throttle,
    IAlerter alerter,
    TimeProvider time,
    IOptions<ScraperOptions> options,
    CrawlMetrics metrics,
    ILogger<DetailCrawlLane> logger) : BackgroundService
{
    private static readonly VisitPriorityOptions PriorityOptions = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CrawlOneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Detail crawl iteration failed unexpectedly");
                await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
            }
        }
    }

    private async Task CrawlOneAsync(CancellationToken ct)
    {
        if (delay.ShouldPause)
        {
            if (throttle.ShouldAlert("detail-lane-paused", time.GetUtcNow()))
            {
                await alerter.RaiseAsync(
                    "Detail crawl paused",
                    $"Three consecutive failures against pricecharting.com; sleeping {options.Value.PauseCooldownMinutes}m before probing again.",
                    ct);
            }

            await Task.Delay(TimeSpan.FromMinutes(options.Value.PauseCooldownMinutes), time, ct);
            // Fall through and attempt one probe; a success clears the pause.
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var card = await PickNextCardAsync(db, ct);
        if (card is null)
        {
            logger.LogInformation("No cards to visit yet — waiting for enumeration");
            await Task.Delay(TimeSpan.FromMinutes(5), time, ct);
            return;
        }

        using var visit = CrawlTracing.Source.StartActivity("card.visit");
        visit?.SetTag("card.id", card.Id);
        visit?.SetTag("card.name", card.Name);

        try
        {
            await VisitAsync(db, card, visit, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Unexpected failures ride the trace, not just the log stream.
            visit?.AddException(e);
            visit?.SetStatus(ActivityStatusCode.Error, e.Message);
            throw;
        }
    }

    private async Task VisitAsync(PokemonDbContext db, Card card, Activity? visit, CancellationToken ct)
    {
        await gate.WaitTurnAsync(ct);
        var started = time.GetTimestamp();
        var fetched = await client.GetAsync(card.Url, ct);
        var now = time.GetUtcNow();
        metrics.RecordRequest("detail", fetched.StatusCode);

        if (fetched.Html is null)
        {
            RecordHttpTrouble(fetched);
            visit?.SetStatus(ActivityStatusCode.Error, $"HTTP {fetched.StatusCode}");
            db.Visits.Add(NewVisit(card, fetched.StatusCode, VisitOutcome.HttpError, shapeHash: null, now));
            await db.SaveChangesAsync(ct);
            return;
        }

        delay.RecordSuccess(fetched.Latency);
        var shapeHash = await RecordShapeAsync(db, card, fetched.Html, now, ct);

        CardDetailPage page;
        try
        {
            page = CardDetailParser.Parse(fetched.Html);
        }
        catch (SchemaDriftException drift)
        {
            metrics.RecordParseFailure();
            visit?.SetStatus(ActivityStatusCode.Error, drift.Message);
            db.ParseFailures.Add(new ParseFailure
            {
                Url = card.Url,
                FetchedAt = now,
                Reason = drift.Message,
                ShapeHash = shapeHash,
            });
            db.Visits.Add(NewVisit(card, fetched.StatusCode, VisitOutcome.ParseFailed, shapeHash, now));
            await db.SaveChangesAsync(ct);
            await CheckFailureRateAsync(db, ct);
            return;
        }

        await WritePageAsync(db, card, page, shapeHash, now, ct);
        metrics.RecordPageParsed();
        metrics.RecordCardVisited();
        metrics.RecordVisitDuration(time.GetElapsedTime(started));
    }

    private async Task WritePageAsync(
        PokemonDbContext db, Card card, CardDetailPage page, string shapeHash, DateTimeOffset now, CancellationToken ct)
    {
        var priceRows = await db.PriceMonths.AsNoTracking()
            .Where(p => p.CardId == card.Id).ToListAsync(ct);
        var lastPrices = priceRows
            .GroupBy(p => (p.Tier, p.Month))
            .ToDictionary(g => g.Key, g => g.MaxBy(p => p.ObservedAt)!.PriceCents);

        var popRows = await db.Populations.AsNoTracking()
            .Where(p => p.CardId == card.Id).ToListAsync(ct);
        var lastPops = popRows
            .GroupBy(p => (p.Grader, p.Grade))
            .ToDictionary(g => g.Key, g => g.MaxBy(p => p.ObservedAt)!.Population);

        var newPrices = ChangeOnlyPlanner.NewPricePoints(card.Id, page.Chart, lastPrices, now);
        var newPops = page.Population is null
            ? []
            : ChangeOnlyPlanner.NewPopulationCells(card.Id, page.Population, lastPops, now);

        foreach (var violation in GradeMonotonicity.Violations(page.Chart))
        {
            logger.LogWarning(
                "Monotonicity violation on card {CardId}: {Lower} {LowerCents}c > {Higher} {HigherCents}c",
                card.Id, violation.Lower, violation.LowerCents, violation.Higher, violation.HigherCents);
        }

        var observation = SalesObservation.From(page.Sales, card.LastVisitedAt, now);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.PriceMonths.AddRange(newPrices);
        db.Populations.AddRange(newPops);
        var newSales = await new SaleWriter(db).AppendNewAsync(card.Id, page.Sales, now, ct);
        db.Visits.Add(NewVisit(card, 200, VisitOutcome.Parsed, shapeHash, now));

        card.LastVisitedAt = now;
        card.LastSeenAt = now;
        card.ObservedSalesPerDay = observation.SalesPerDay;
        card.AnyBucketAtCap = observation.AnyBucketAtCap;
        card.ImageHash ??= page.ImageHash;

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        metrics.RecordRowsAppended(newPrices.Count, newPops.Count, newSales);

        logger.LogInformation(
            "Card {CardId} ({Name}): +{Prices} price rows, +{Pops} pop cells, +{Sales} sales, churn {Churn:F2}/d{Cap}",
            card.Id, card.Name, newPrices.Count, newPops.Count, newSales,
            observation.SalesPerDay, observation.AnyBucketAtCap ? ", AT CAP" : "");
    }

    /// <summary>Unvisited first; otherwise the tested priority score over the
    /// stalest 500 plus every cap-hit card.</summary>
    private async Task<Card?> PickNextCardAsync(PokemonDbContext db, CancellationToken ct)
    {
        var unvisited = await db.Cards
            .Where(c => c.LastVisitedAt == null)
            .OrderBy(c => c.Id)
            .FirstOrDefaultAsync(ct);
        if (unvisited is not null)
        {
            return unvisited;
        }

        var candidates = await db.Cards
            .OrderBy(c => c.LastVisitedAt)
            .Take(500)
            .ToListAsync(ct);
        var capHits = await db.Cards
            .Where(c => c.AnyBucketAtCap)
            .OrderBy(c => c.LastVisitedAt)
            .Take(50)
            .ToListAsync(ct);

        var now = time.GetUtcNow();
        if (candidates.Count > 0 && candidates[0].LastVisitedAt is { } oldest)
        {
            metrics.SetQueueStaleness(now - oldest);
        }

        return candidates.Concat(capHits)
            .DistinctBy(c => c.Id)
            .MaxBy(c => VisitPriority.Score(
                new CardVisitState
                {
                    LastVisitedAt = c.LastVisitedAt,
                    ObservedSalesPerDay = c.ObservedSalesPerDay,
                    AnyBucketAtCap = c.AnyBucketAtCap,
                },
                now,
                PriorityOptions));
    }

    /// <summary>Fingerprints the page; a never-before-seen shape is archived
    /// to disk and alerted — it is the site telling us it changed.</summary>
    private async Task<string> RecordShapeAsync(
        PokemonDbContext db, Card card, string html, DateTimeOffset now, CancellationToken ct)
    {
        var print = PageFingerprint.OfCardDetailPage(html);
        var known = await db.Shapes.FindAsync([print.Hash], ct);
        if (known is not null)
        {
            known.LastSeenAt = now;
            return print.Hash;
        }

        db.Shapes.Add(new PageShape
        {
            Hash = print.Hash,
            ShapeJson = print.ShapeJson,
            SampleUrl = card.Url,
            FirstSeenAt = now,
            LastSeenAt = now,
        });

        Directory.CreateDirectory(options.Value.ShapeArchiveDirectory);
        var archivePath = Path.Combine(options.Value.ShapeArchiveDirectory, $"{print.Hash}.html");
        await File.WriteAllTextAsync(archivePath, html, ct);

        if (throttle.ShouldAlert($"new-page-shape:{print.Hash}", now))
        {
            await alerter.RaiseAsync(
                "New page shape observed",
                $"Card detail pages have a structure never seen before.\nSample: {card.Url}\nShape: {print.ShapeJson}\nArchived: {archivePath}",
                ct);
        }

        return print.Hash;
    }

    private async Task CheckFailureRateAsync(PokemonDbContext db, CancellationToken ct)
    {
        var recent = await db.Visits.AsNoTracking()
            .Where(v => v.Kind == PageKind.CardDetail)
            .OrderByDescending(v => v.FetchedAt)
            .Take(100)
            .Select(v => v.Outcome)
            .ToListAsync(ct);

        if (recent.Count < 20)
        {
            return;
        }

        var failureRate = (double)recent.Count(o => o == VisitOutcome.ParseFailed) / recent.Count;
        if (failureRate > options.Value.ParseFailureAlertThreshold
            && throttle.ShouldAlert("parse-failure-spike", time.GetUtcNow()))
        {
            await alerter.RaiseAsync(
                "Parse failure rate spike",
                $"{failureRate:P0} of the last {recent.Count} detail pages failed to parse — "
                + "pricecharting.com has probably changed its markup. See parse_failures and shapes/.",
                ct);
        }
    }

    private void RecordHttpTrouble(FetchResult fetched)
    {
        if (fetched.StatusCode is 429 or 503)
        {
            delay.RecordRateLimited(fetched.RetryAfter);
        }
        else
        {
            delay.RecordFailure(fetched.RetryAfter);
        }
    }

    private static PageVisit NewVisit(Card card, int status, VisitOutcome outcome, string? shapeHash, DateTimeOffset now) =>
        new()
        {
            Kind = PageKind.CardDetail,
            Url = card.Url,
            CardId = card.Id,
            FetchedAt = now,
            HttpStatus = status,
            Outcome = outcome,
            ShapeHash = shapeHash,
        };
}
