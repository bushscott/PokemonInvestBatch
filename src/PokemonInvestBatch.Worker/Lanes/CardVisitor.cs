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
/// The whole errand minus the picking and the gate — fetch one card's detail
/// page, parse it, write everything it contains in one transaction, attribute
/// any failure. The detail lane's turn and an express visit both come through
/// here, so there is exactly one truth for what a visit does to a card:
/// strikes, quarantine, the not-a-card verdict, and the drift ledger behave
/// identically whichever path delivered the visit.
/// </summary>
public sealed class CardVisitor(
    PriceChartingClient client,
    AdaptiveDelay delay,
    IncidentThrottle throttle,
    IAlerter alerter,
    PageFingerprintArchive fingerprints,
    TimeProvider time,
    IOptions<ScraperOptions> options,
    CrawlMetrics metrics,
    ILogger<CardVisitor> logger)
{
    /// <summary>What the visit concluded, for callers that answer to someone.
    /// The lane discards it; the express endpoint reports it to its caller.</summary>
    public sealed record VisitResult(VisitOutcome Outcome, int HttpStatus);

    public async Task<VisitResult> VisitAsync(
        PokemonDbContext db, Card card, Activity? visit, string laneTag, CancellationToken ct)
    {
        // Read before the write path clears it: the wait metric measures
        // filed → served, and "served" is the commit about to happen.
        var requestedAt = card.RefreshRequestedAt;

        var started = time.GetTimestamp();
        var fetched = await client.GetAsync(card.Url, ct);
        var now = time.GetUtcNow();
        fetched.RecordOutcome(metrics, delay, laneTag);

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
            db.Visits.Add(NewVisit(card, fetched.StatusCode, VisitOutcome.HttpError, fingerprintHash: null, now));
            if (QuarantinePolicy.IsCardAttributable(fetched.StatusCode))
            {
                await RecordStrikeAsync(card, $"http-{fetched.StatusCode}", now, ct);
            }

            await db.SaveChangesAsync(ct);
            return new VisitResult(VisitOutcome.HttpError, fetched.StatusCode);
        }

        var fingerprintHash = await fingerprints.RecordAsync(db, card.Url, fetchedPage.Html, now, ct);

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
        catch (NotACardPageException verdict)
        {
            await RetireNotACardAsync(db, card, verdict, fetched.StatusCode, fingerprintHash, visit, now, ct);
            return new VisitResult(VisitOutcome.NotACard, fetched.StatusCode);
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
                FingerprintHash = fingerprintHash,
            });
            db.Visits.Add(NewVisit(card, fetched.StatusCode, VisitOutcome.ParseFailed, fingerprintHash, now));
            await RecordStrikeAsync(card, "parse", now, ct);
            await db.SaveChangesAsync(ct);
            await CheckFailureRateAsync(db, ct);
            return new VisitResult(VisitOutcome.ParseFailed, fetched.StatusCode);
        }

        await WritePageAsync(db, card, page, fingerprintHash, now, ct);
        metrics.RecordPageParsed();
        metrics.RecordCardVisited();
        metrics.RecordVisitDuration(time.GetElapsedTime(started));
        if (requestedAt is { } asked)
        {
            metrics.RecordRefreshServed(now - asked);
        }

        return new VisitResult(VisitOutcome.Parsed, fetched.StatusCode);
    }

    /// <summary>Caller saves; the card is tracked, so the streak rides the
    /// same SaveChanges as the visit row that earned it. Public because the
    /// lane's unattributed-failure path writes a strike through a fresh
    /// context after a visit died mid-flight.</summary>
    public async Task RecordStrikeAsync(Card card, string reason, DateTimeOffset now, CancellationToken ct)
    {
        card.FailureStreak++;
        var until = QuarantinePolicy.QuarantineUntil(card.FailureStreak, now);
        if (until is null)
        {
            return;
        }

        // Joining the bench is news; still sitting on it is not. Re-benching
        // used to re-fire this counter on every failed retry, which is how
        // three broken pages once held the 24h dashboard panel at a permanent
        // ~144 — the count now matches the panel's own words, "cards added".
        var joiningBench = card.QuarantinedUntil is not { } sentence || sentence <= now;
        card.QuarantinedUntil = until;
        if (joiningBench)
        {
            metrics.RecordCardQuarantined(reason);
        }
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

    /// <summary>Commits the page, then says what changed. The writing lives in
    /// CardPageWriter; what remains here is the narration — metrics, the
    /// summary line, and the two findings worth waking someone for.</summary>
    private async Task WritePageAsync(
        PokemonDbContext db, Card card, CardDetailPage page, string fingerprintHash, DateTimeOffset now, CancellationToken ct)
    {
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

        var written = await CardPageWriter.WriteAsync(db, card, page, fingerprintHash, now, ct);
        metrics.RecordRowsAppended(written.NewPriceRows, written.NewPopulationCells, written.NewSales);

        logger.Log(
            written.Observation.AnyBucketAtCap ? LogLevel.Warning : LogLevel.Information,
            "Card {CardId} ({Name}): +{Prices} price rows, +{Pops} pop cells, +{Sales} sales, churn {Churn:F2}/d{Cap}",
            card.Id, card.Name, written.NewPriceRows, written.NewPopulationCells, written.NewSales,
            written.Observation.SalesPerDay, written.Observation.AnyBucketAtCap ? ", AT CAP" : "");

        if (page.Population is not null)
        {
            await FlagPopulationAnomaliesAsync(card, page.Population, written.PreviousPopulations, ct);
        }

        // Naming the bucket is the difference between an alert someone can act
        // on and one they have to go query for — and buckets are not all the
        // same size, so "which grade" is also the only honest way to say how
        // much is gone.
        if (written.NewlyAtCap
            && written.Observation.CappedTier is { } cappedTier
            && throttle.ShouldAlert("sales-lost", now))
        {
            await alerter.RaiseAsync(
                "Sales lost to a hot card",
                $"Card {card.Id} ({card.Name}) is missing sales data because it outsold our visit "
                + $"pace: its {cappedTier} sale page turned over completely between visits — not "
                + $"one row we already held is still on it, so everything in between is gone for "
                + $"good. It is fast-tracked until its buckets calm down.\n"
                + $"https://www.pricecharting.com{card.Url}",
                ct);
        }

        // The graduated warning. A cap hit only speaks once the rows are gone,
        // so a bucket that came back with a handful of rows still in common is
        // the last chance to hear about it in advance. Margin zero is the loss
        // itself and is reported above, not here.
        //
        // Observed-only for now: it names the card and feeds a counter, but does
        // not enqueue anything. The volume estimate behind NearMissMargin was
        // measured on graded buckets alone — Ungraded page size is not knowable
        // from stored history — so the real rate gets watched before it is
        // allowed to spend visits.
        if (written.Observation is { NarrowestMargin: { } margin, NarrowestTier: { } narrowTier }
            && margin > 0
            && margin <= options.Value.NearMissMargin)
        {
            metrics.RecordBucketNearMiss(narrowTier);
            logger.LogWarning(
                "Near miss on card {CardId} ({Name}): its {Tier} page came back with only "
                + "{Margin} row(s) we already held — one quieter day and it would have rolled",
                card.Id, card.Name, narrowTier, margin);
        }

        if (written.NewlyAtCap)
        {
            await FastTrackSetSiblingsAsync(db, card, now, ct);
        }
    }

    /// <summary>
    /// Hype is set-shaped — the Aug 2026 losses were two cards in one set.
    /// When a bucket caps, the set's hottest known sellers are stamped with a
    /// refresh ask, so the crawl sees each of them within its next few polite
    /// slots at the tier right behind burn-window prevention. Best-effort: the
    /// capped card's own visit already committed, and a failed stamp costs
    /// only the head start, never the visit.
    /// </summary>
    private async Task FastTrackSetSiblingsAsync(
        PokemonDbContext db, Card card, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var siblingIds = await VisitCandidatePool
                .HottestSetSiblings(db, card.SetId, card.Id)
                .ToListAsync(ct);
            if (siblingIds.Count == 0)
            {
                return;
            }

            // The repeated null check makes the stamp idempotent against a
            // racing express visit clearing an ask between select and update.
            var stamped = await db.Cards
                .Where(c => siblingIds.Contains(c.Id) && c.RefreshRequestedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.RefreshRequestedAt, now), ct);
            if (stamped == 0)
            {
                return;
            }

            var slug = await db.Sets
                .Where(s => s.Id == card.SetId)
                .Select(s => s.Slug)
                .FirstOrDefaultAsync(ct) ?? "unknown";
            logger.LogWarning(
                "Set contagion: card {CardId} ({Name}) capped in set {SetSlug} — fast-tracked its {Count} hottest sellers",
                card.Id, card.Name, slug, stamped);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Set-contagion fast-track failed for card {CardId}", card.Id);
        }
    }

    /// <summary>
    /// A page that parsed cleanly and simply is not a card — a console, a game,
    /// an accessory the catalog filed under Pokemon. The trail is recorded like
    /// a parse failure, but deliberately NOT counted as one: the parse-failure
    /// rate is the site-changed alarm, and a miscatalogued set must never read
    /// as an outage while the crawl is perfectly healthy.
    ///
    /// The verdict is permanent — no strike, no sentence, no comeback date — so
    /// the card leaves the rotation for good instead of returning every ten
    /// minutes forever the way a benched card does.
    /// </summary>
    private async Task RetireNotACardAsync(
        PokemonDbContext db,
        Card card,
        NotACardPageException verdict,
        int statusCode,
        string fingerprintHash,
        Activity? visit,
        DateTimeOffset now,
        CancellationToken ct)
    {
        visit?.SetStatus(ActivityStatusCode.Error, verdict.Message);

        // No parse_failures row. That table is the drift ledger — the thing you
        // grep when the site has moved and you need to know what broke — and
        // filling it with consoles makes the one investigation it exists for
        // harder. The verdict lives on the card, the visit records that it
        // happened, and the reason goes to the log and the alert.

        // NotACard, never ParseFailed: CheckFailureRateAsync counts ParseFailed
        // rows in the last hundred visits, so filing these there would let a
        // miscatalogued set raise the alarm that means "the site changed and the
        // parser is now blind" — the exact false emergency this path exists to
        // avoid. Six of them in one window is all it would take.
        db.Visits.Add(NewVisit(card, statusCode, VisitOutcome.NotACard, fingerprintHash, now));

        card.NotACardAt = now;

        // Any streak and sentence are cleared on the way out. They described a
        // card that kept failing; this was never a card, and leaving them set
        // would keep it counted among the benched forever. A pending refresh
        // ask goes with them — there is no card left to refresh.
        card.FailureStreak = 0;
        card.QuarantinedUntil = null;
        card.RefreshRequestedAt = null;
        await db.SaveChangesAsync(ct);

        var slug = await db.Sets
            .Where(s => s.Id == card.SetId)
            .Select(s => s.Slug)
            .FirstOrDefaultAsync(ct) ?? "unknown";
        metrics.RecordNotACard(slug);

        logger.LogWarning(
            "Card {CardId} ({Name}) is not a card and has been retired from set {SetSlug} — {Reason}",
            card.Id, card.Name, slug, verdict.Message);

        // Throttled per SET, not per card: one miscatalogued set should produce
        // one alert you can act on, not seventeen you learn to dismiss.
        if (throttle.ShouldAlert($"not-a-card:{slug}", now))
        {
            await alerter.RaiseAsync(
                "A set in the catalog is not cards",
                $"Card {card.Id} ({card.Name}) in set '{slug}' is not a card: {verdict.Message}\n\n"
                + $"{options.Value.BaseUrl}{card.Url}\n\n"
                + "It has been retired and will not be visited again. If the whole set is "
                + $"miscatalogued, add \"{slug}\" to blacklist.json so enumeration stops "
                + "re-walking it; its siblings retire themselves as their visits come up.",
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

    private async Task CheckFailureRateAsync(PokemonDbContext db, CancellationToken ct)
    {
        var recent = await db.Visits.AsNoTracking()
            .Where(v => v.Kind == PageKind.CardDetail)
            .OrderByDescending(v => v.FetchedAt)
            .Take(100)
            .Select(v => v.Outcome)
            .ToListAsync(ct);

        var failures = recent.Count(o => o == VisitOutcome.ParseFailed);
        if (!ParseFailureRate.IsSpike(failures, recent.Count, options.Value.ParseFailureAlertThreshold)
            || !throttle.ShouldAlert("parse-failure-spike", time.GetUtcNow()))
        {
            return;
        }

        await alerter.RaiseAsync(
            "Parse failure rate spike",
            $"{(double)failures / recent.Count:P0} of the last {recent.Count} detail pages failed to "
            + "parse — pricecharting.com has probably changed its markup. See parse_failures and fingerprints/.",
            ct);
    }

    private static PageVisit NewVisit(Card card, int status, VisitOutcome outcome, string? fingerprintHash, DateTimeOffset now) =>
        new()
        {
            Kind = PageKind.CardDetail,
            Url = card.Url,
            CardId = card.Id,
            FetchedAt = now,
            HttpStatus = status,
            Outcome = outcome,
            FingerprintHash = fingerprintHash,
        };
}
