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
/// The main lane. A "visit" is the whole errand for one card — fetch its
/// detail page (one HTTP request) through the shared polite gate, parse it,
/// write everything it contains in one transaction, mark the card checked.
/// Nothing is written from a page that failed any check. See GLOSSARY.md
/// for the visit/request vocabulary.
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

    private readonly SameCardFailureBreaker breaker = new();

    private readonly BenchRecheck benchRecheck =
        new(TimeSpan.FromMinutes(options.Value.BenchRecheckIntervalMinutes));

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

        // A still-benched card can only reach us through the bench recheck;
        // remembered so its failures skip the breaker below.
        var isBenchRetry = card.QuarantinedUntil is { } benchedUntil
            && benchedUntil > time.GetUtcNow();

        // The polite wait happens outside the span: card.visit measures work
        // (fetch through commit), not the voluntary sleep before it — same
        // boundary as the visit-duration histogram.
        await gate.WaitTurnAsync(ct);

        using var visit = CrawlTracing.Source.StartActivity("card.visit");
        visit?.SetTag("card.id", card.Id);
        visit?.SetTag("card.name", card.Name);
        // The page's path rides the span so a slow visit can be traced
        // straight to the card page that caused it.
        visit?.SetTag("url.path", card.Url);

        // Every log line written during the visit — EF's transaction chatter
        // included — carries the card, so no mid-visit error ever needs
        // trace archaeology to attribute.
        using var scope = logger.BeginScope("Visiting {CardUrl}", card.Url);

        try
        {
            await VisitAsync(db, card, visit, ct);
            breaker.Reset();
            if (isBenchRetry)
            {
                // A cleared bench is the proof of healing; anything else —
                // parse failure, HTTP trouble — means stand down. The visit
                // itself already recorded why.
                if (card.QuarantinedUntil is null)
                {
                    benchRecheck.RecordSuccess();
                }
                else
                {
                    benchRecheck.RecordFailure(time.GetUtcNow());
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown caught the visit mid-flight: the transaction rolls
            // back whole and LastVisitedAt never advanced. Said out loud so
            // EF's exception-less "error using a transaction" has a witness.
            logger.LogInformation(
                "Visit of {CardUrl} interrupted by shutdown — the card returns to the rotation",
                card.Url);
            throw;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Unexpected failures ride the trace, not just the log stream.
            visit?.AddException(e);
            visit?.SetStatus(ActivityStatusCode.Error, e.Message);
            if (isBenchRetry)
            {
                // The breaker exists to attribute repeat failures to a card;
                // a benched card is already attributed. One failed retry
                // re-benches it immediately with the doubled sentence —
                // sending it behind the other benched cards — and stands
                // the recheck down for the interval.
                benchRecheck.RecordFailure(time.GetUtcNow());
                await StrikeUnattributedAsync(card.Id, ct);
            }
            else if (breaker.RecordUnexpectedFailure(card.Id))
            {
                await StrikeUnattributedAsync(card.Id, ct);
            }

            // Rethrown so ExecuteAsync still logs the full exception — the
            // strike above is attribution, never suppression.
            throw;
        }
    }

    /// <summary>
    /// The visit died before any of its own bookkeeping could run, so the
    /// strike is written through a fresh context — the one that failed may
    /// hold a poisoned change tracker or a broken connection.
    /// </summary>
    private async Task StrikeUnattributedAsync(long cardId, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var card = await db.Cards.FirstAsync(c => c.Id == cardId, ct);
            await RecordStrikeAsync(card, "unexpected", time.GetUtcNow(), ct);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e,
                "Could not record unexpected-failure strike for card {CardId}", cardId);
        }
    }

    private async Task VisitAsync(PokemonDbContext db, Card card, Activity? visit, CancellationToken ct)
    {
        var started = time.GetTimestamp();
        var fetched = await client.GetAsync(card.Url, ct);
        var now = time.GetUtcNow();
        fetched.RecordOutcome(metrics, delay, "card pages");

        if (fetched is not FetchedPage fetchedPage)
        {
            if (fetched is FetchFailure { RedirectTarget: { } movedTo })
            {
                logger.LogWarning(
                    "Card {CardId} ({Name}) fetch returned HTTP {Status} redirecting to {RedirectTarget}",
                    card.Id, card.Name, fetched.StatusCode, movedTo);
            }
            else
            {
                logger.LogWarning(
                    "Card {CardId} ({Name}) fetch returned HTTP {Status}",
                    card.Id, card.Name, fetched.StatusCode);
            }
            visit?.SetStatus(ActivityStatusCode.Error, $"HTTP {fetched.StatusCode}");
            db.Visits.Add(NewVisit(card, fetched.StatusCode, VisitOutcome.HttpError, shapeHash: null, now));
            if (QuarantinePolicy.IsCardAttributable(fetched.StatusCode))
            {
                await RecordStrikeAsync(card, $"http-{fetched.StatusCode}", now, ct);
            }

            await db.SaveChangesAsync(ct);
            return;
        }

        var shapeHash = await RecordShapeAsync(db, card, fetchedPage.Html, now, ct);

        CardDetailPage page;
        try
        {
            // Own span so parse time is visible next to fetch and SQL in the
            // where-time-goes breakdown instead of hiding in card.visit's gap.
            using (CrawlTracing.Source.StartActivity("card.parse"))
            {
                page = CardDetailParser.Parse(fetchedPage.Html);
            }
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
        // Phase span: this is the visit's only O(card history) section, so it
        // gets its own band in the where-time-goes stack to watch it grow.
        Dictionary<(PriceTier Tier, DateOnly Month), int> lastPrices;
        Dictionary<(string Grader, short Grade), int> lastPops;
        using (CrawlTracing.Source.StartActivity("card.load_history"))
        {
            var priceRows = await db.PriceMonths.AsNoTracking()
                .Where(p => p.CardId == card.Id).ToListAsync(ct);
            lastPrices = priceRows
                .GroupBy(p => (p.Tier, p.Month))
                .ToDictionary(g => g.Key, g => g.MaxBy(p => p.ObservedAt)!.PriceCents);

            var popRows = await db.Populations.AsNoTracking()
                .Where(p => p.CardId == card.Id).ToListAsync(ct);
            lastPops = popRows
                .GroupBy(p => (p.Grader, p.Grade))
                .ToDictionary(g => g.Key, g => g.MaxBy(p => p.ObservedAt)!.Population);
        }

        var newPrices = ChangeOnlyPlanner.NewPricePoints(card.Id, page.Chart, lastPrices, now);
        var newPops = page.Population is null
            ? []
            : ChangeOnlyPlanner.NewPopulationCells(card.Id, page.Population, lastPops, now);

        var violations = GradeMonotonicity.Violations(page.Chart);
        metrics.RecordMonotonicityViolations(violations.Count);
        foreach (var violation in violations)
        {
            // Information, not Warning: a single out-of-order ladder is thin-
            // market noise and is expected. The alarmable signal is a step
            // change in the crawl.monotonicity_violations metric, never one card.
            logger.LogInformation(
                "Monotonicity violation on card {CardId}: {Lower} {LowerCents}c > {Higher} {HigherCents}c",
                card.Id, violation.Lower, violation.LowerCents, violation.Higher, violation.HigherCents);
        }

        if (page.Population is not null)
        {
            await FlagPopulationAnomaliesAsync(card, page.Population, lastPops, ct);
        }

        var observation = SalesObservation.From(page.Sales, card.LastVisitedAt, now);

        // The flip into cap is the moment loss was proven; the fast-track
        // revisits that follow re-observe hot buckets while the cure runs,
        // and must not re-raise.
        var newlyAtCap = observation.AnyBucketAtCap && !card.AnyBucketAtCap;

        int newSales;
        using (CrawlTracing.Source.StartActivity("card.write"))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            db.PriceMonths.AddRange(newPrices);
            db.Populations.AddRange(newPops);
            newSales = await new SaleWriter(db).AppendNewAsync(card.Id, page.Sales, now, ct);
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
        }

        metrics.RecordRowsAppended(newPrices.Count, newPops.Count, newSales);

        logger.Log(
            observation.AnyBucketAtCap ? LogLevel.Warning : LogLevel.Information,
            "Card {CardId} ({Name}): +{Prices} price rows, +{Pops} pop cells, +{Sales} sales, churn {Churn:F2}/d{Cap}",
            card.Id, card.Name, newPrices.Count, newPops.Count, newSales,
            observation.SalesPerDay, observation.AnyBucketAtCap ? ", AT CAP" : "");

        if (newlyAtCap && throttle.ShouldAlert("sales-lost", now))
        {
            await alerter.RaiseAsync(
                "Sales lost to a hot card",
                $"Card {card.Id} ({card.Name}) is missing sales data because it outsold our visit "
                + $"pace: the sale page completely turned over between visits, and anything older "
                + $"than the newest {SalesObservation.BucketCap} rows is gone for good. It is "
                + $"fast-tracked until its buckets calm down.\n"
                + $"https://www.pricecharting.com{card.Url}",
                ct);
        }
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

    /// <summary>Runs the queries VisitSelection's ranking needs, then executes
    /// the choice it returns. Only the chosen card is loaded for real — the
    /// visit writes to it; the ~600 candidates cross the wire as three columns.</summary>
    private async Task<Card?> PickNextCardAsync(PokemonDbContext db, CancellationToken ct)
    {
        var now = time.GetUtcNow();

        long? benchRetryId = null;
        if (benchRecheck.IsSlotOpen(now))
        {
            var benched = await VisitCandidatePool.Benched(db, now).ToListAsync(ct);
            benchRetryId = benchRecheck.TrySelect(benched, now);
        }

        IReadOnlyList<VisitCandidate> candidates = [];
        if (benchRetryId is null)
        {
            candidates = await VisitCandidatePool.LoadAsync(db, now, PriorityOptions, ct);
            if (candidates.Count > 0 && candidates[0].State.LastVisitedAt is { } oldest)
            {
                metrics.SetQueueStaleness(now - oldest);
            }
        }

        var choice = VisitSelection.Choose(benchRetryId, candidates, now, PriorityOptions);
        switch (choice.Kind)
        {
            case VisitChoiceKind.RetryBenched:
                var retried = await db.Cards.FirstAsync(c => c.Id == choice.CardId!.Value, ct);
                logger.LogInformation(
                    "Bench recheck: retrying card {CardId} ({Name}) ahead of its {Until:u} comeback",
                    retried.Id, retried.Name, retried.QuarantinedUntil);
                return retried;

            case VisitChoiceKind.PreferUnvisited:
                var unvisited = await VisitCandidatePool.Eligible(db, now)
                    .Where(c => c.LastVisitedAt == null)
                    .OrderBy(c => c.Id)
                    .FirstOrDefaultAsync(ct);
                if (unvisited is not null)
                {
                    return unvisited;
                }

                // The backlog is drained; the runner-up gets the slot after all.
                return choice.CardId is { } fallback
                    ? await db.Cards.FirstAsync(c => c.Id == fallback, ct)
                    : null;

            default:
                return await db.Cards.FirstAsync(c => c.Id == choice.CardId!.Value, ct);
        }
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
