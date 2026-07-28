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

        // The polite wait happens outside the span: card.visit measures work
        // (fetch through commit), not the voluntary sleep before it — same
        // boundary as the visit-duration histogram.
        await gate.WaitTurnAsync(ct);

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
        var started = time.GetTimestamp();
        var fetched = await client.GetAsync(card.Url, ct);
        var now = time.GetUtcNow();
        metrics.RecordRequest("detail", fetched.StatusCode);

        if (fetched.Html is null)
        {
            RecordHttpTrouble(fetched);
            visit?.SetStatus(ActivityStatusCode.Error, $"HTTP {fetched.StatusCode}");
            db.Visits.Add(NewVisit(card, fetched.StatusCode, VisitOutcome.HttpError, shapeHash: null, now));
            if (QuarantinePolicy.IsCardAttributable(fetched.StatusCode))
            {
                await RecordStrikeAsync(card, $"http-{fetched.StatusCode}", now, ct);
            }

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
            await RecordStrikeAsync(card, "parse", now, ct);
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

        var violations = GradeMonotonicity.Violations(page.Chart);
        metrics.RecordMonotonicityViolations(violations.Count);
        foreach (var violation in violations)
        {
            logger.LogWarning(
                "Monotonicity violation on card {CardId}: {Lower} {LowerCents}c > {Higher} {HigherCents}c",
                card.Id, violation.Lower, violation.LowerCents, violation.Higher, violation.HigherCents);
        }

        if (page.Population is not null)
        {
            await FlagPopulationAnomaliesAsync(card, page.Population, lastPops, ct);
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
        card.FailureStreak = 0;
        card.QuarantinedUntil = null;

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        metrics.RecordRowsAppended(newPrices.Count, newPops.Count, newSales);

        logger.LogInformation(
            "Card {CardId} ({Name}): +{Prices} price rows, +{Pops} pop cells, +{Sales} sales, churn {Churn:F2}/d{Cap}",
            card.Id, card.Name, newPrices.Count, newPops.Count, newSales,
            observation.SalesPerDay, observation.AnyBucketAtCap ? ", AT CAP" : "");
    }

    /// <summary>Caller saves; the card is tracked, so the streak rides the
    /// same SaveChanges as the visit row that earned it.</summary>
    private async Task RecordStrikeAsync(Card card, string reason, DateTimeOffset now, CancellationToken ct)
    {
        card.FailureStreak++;
        var until = QuarantinePolicy.QuarantineUntil(card.FailureStreak, now);
        if (until is null)
        {
            return;
        }

        card.QuarantinedUntil = until;
        metrics.RecordCardQuarantined(reason);
        logger.LogWarning(
            "Card {CardId} ({Name}) quarantined until {Until:u} after {Streak} consecutive failures ({Reason})",
            card.Id, card.Name, until.Value, card.FailureStreak, reason);

        if (throttle.ShouldAlert("card-quarantined", now))
        {
            await alerter.RaiseAsync(
                "Card quarantined",
                $"Card {card.Id} ({card.Name}) failed {card.FailureStreak} visits in a row ({reason}) "
                + $"and is benched until {until.Value:u}. Its page is broken in a way the rest of the "
                + $"corpus is not — see parse_failures/visits for {card.Url}.",
                ct);
        }
    }

    /// <summary>The census rows are appended regardless — history is
    /// append-only and the restated numbers are what the source now says —
    /// but a restatement must never be read as market demand, so it is
    /// flagged loudly: metric, warning, and one alert per incident.</summary>
    private async Task FlagPopulationAnomaliesAsync(
        Card card,
        PopulationReport population,
        IReadOnlyDictionary<(string Grader, short Grade), int> lastPops,
        CancellationToken ct)
    {
        var anomalies = PopulationRestatement.Anomalies(population, lastPops);
        foreach (var anomaly in anomalies)
        {
            metrics.RecordPopAnomaly(anomaly.Grader, anomaly.Kind == PopulationAnomalyKind.Spike ? "spike" : "decrease");
            logger.LogWarning(
                "Population {Kind} on card {CardId}: {Grader} grade {Grade} went {Previous} -> {Current}",
                anomaly.Kind, card.Id, anomaly.Grader, anomaly.Grade, anomaly.Previous, anomaly.Current);
        }

        if (anomalies.Count > 0 && throttle.ShouldAlert("pop-restatement", time.GetUtcNow()))
        {
            var lines = anomalies.Select(a =>
                $"  {a.Grader} grade {a.Grade}: {a.Previous} -> {a.Current} ({a.Kind})");
            await alerter.RaiseAsync(
                "Population census restatement",
                $"Card {card.Id} ({card.Name}) census moved beyond grading pace — the grader "
                + "changed its counting, not the market. Rows are appended; treat the jump as a "
                + $"source change in analytics.\n{string.Join('\n', lines)}",
                ct);
        }
    }

    /// <summary>Unvisited first; otherwise the tested priority score over the
    /// stalest 500 plus every cap-hit card. Quarantined cards are invisible
    /// until their sentence lapses.</summary>
    private async Task<Card?> PickNextCardAsync(PokemonDbContext db, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var eligible = db.Cards
            .Where(c => c.QuarantinedUntil == null || c.QuarantinedUntil < now);

        var unvisited = await eligible
            .Where(c => c.LastVisitedAt == null)
            .OrderBy(c => c.Id)
            .FirstOrDefaultAsync(ct);
        if (unvisited is not null)
        {
            return unvisited;
        }

        var candidates = await eligible
            .OrderBy(c => c.LastVisitedAt)
            .Take(500)
            .ToListAsync(ct);
        var capHits = await eligible
            .Where(c => c.AnyBucketAtCap)
            .OrderBy(c => c.LastVisitedAt)
            .Take(50)
            .ToListAsync(ct);
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
