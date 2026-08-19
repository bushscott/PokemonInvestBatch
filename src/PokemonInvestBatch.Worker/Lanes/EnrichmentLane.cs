using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PokemonInvestBatch.Application.Enrichment;
using PokemonInvestBatch.Infrastructure.Enrichment;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Worker.Lanes;

/// <summary>What one enrichment sweep computed and wrote.</summary>
public sealed record EnrichmentSweepResult
{
    public required string Version { get; init; }

    public required int Cards { get; init; }

    public required int RowsWritten { get; init; }

    public required IReadOnlyDictionary<TcgdexMatchStatus, int> Verdicts { get; init; }
}

/// <summary>
/// The TCGdex metadata join (ADR-0009): collector number and official set
/// size per card, with an explicit per-card match status. Runs entirely
/// against the local mirror — the one time it touches the network is the
/// initial mirror fetch (a different host, so outside the politeness gate,
/// like <see cref="ImageLane"/>). Verdicts are change-only appends: a sweep
/// over unchanged inputs writes nothing.
/// </summary>
public sealed class EnrichmentLane(
    IDbContextFactory<PokemonDbContext> dbFactory,
    IHttpClientFactory httpFactory,
    TimeProvider time,
    IOptions<ScraperOptions> options,
    ILogger<EnrichmentLane> logger) : BackgroundService
{
    public const string HttpClientName = "tcgdex";

    /// <summary>Inserted per SaveChanges so the first sweep's ~90k rows do
    /// not sit in one change tracker.</summary>
    private const int InsertChunk = 2000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Enrichment sweep failed");
            }

            await Task.Delay(
                TimeSpan.FromHours(options.Value.TcgdexEnrichmentIntervalHours), time, stoppingToken);
        }
    }

    public async Task<EnrichmentSweepResult> RunSweepAsync(CancellationToken ct)
    {
        var scraper = options.Value;

        // EnsureAsync (not a bare Exists-then-FetchAsync) because PokedexLane
        // shares this same mirror directory and calls it too — the gate
        // inside EnsureAsync is what keeps the two lanes from racing to
        // fetch it at once (see TcgdexMirror's class doc). A factory delegate
        // goes in, not a pre-built HttpClient: EnsureAsync only actually
        // calls it on the branch that fetches, so a warm sweep (the common
        // case forever after the first) never pays for a client it will not
        // use — passing httpFactory.CreateClient(...) directly here would
        // evaluate eagerly, before EnsureAsync ever got a say.
        await TcgdexMirror.EnsureAsync(
            () => httpFactory.CreateClient(HttpClientName),
            scraper.TcgdexBaseUrl,
            "en",
            scraper.TcgdexMirrorDirectory,
            time,
            logger,
            ct);

        var (catalog, manifest) = await TcgdexMirror.LoadAsync(scraper.TcgdexMirrorDirectory, ct);

        // Same posture as the blacklist: user-maintained JSON, absent means
        // empty, malformed refuses loudly (a silently dropped alias would
        // quietly unmap a curated set).
        var aliases = File.Exists(scraper.TcgdexSetAliasesPath)
            ? TcgdexSetAliases.Parse(await File.ReadAllTextAsync(scraper.TcgdexSetAliasesPath, ct))
            : new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var sets = await db.Sets
            .Select(s => new { s.Id, s.Slug, s.Name })
            .ToListAsync(ct);
        var map = SetMapper.Resolve(sets.Select(s => (s.Slug, s.Name)), catalog, aliases);
        var entryBySetId = sets.ToDictionary(s => s.Id, s => map[s.Slug]);

        // Not-a-card pages are consoles and accessories — there is nothing to
        // enrich. Delisted cards keep their history and stay enrichable.
        var cards = await db.Cards
            .Where(c => c.NotACardAt == null)
            .Select(c => new { c.Id, c.Name, c.SetId })
            .ToListAsync(ct);

        var latest = await LoadLatestVerdictsAsync(db, ct);

        var computedAt = time.GetUtcNow();
        var verdictCounts = new Dictionary<TcgdexMatchStatus, int>();
        var inserts = new List<TcgdexEnrichment>();
        foreach (var card in cards)
        {
            ct.ThrowIfCancellationRequested();
            var verdict = TcgdexMatcher.Match(card.Name, entryBySetId[card.SetId], catalog);
            verdictCounts[verdict.Status] = verdictCounts.GetValueOrDefault(verdict.Status) + 1;

            // Change-only: record equality over the six verdict fields is the
            // test, so a re-run against an unchanged mirror writes nothing.
            if (latest.TryGetValue(card.Id, out var previous) && previous == verdict)
            {
                continue;
            }

            inserts.Add(new TcgdexEnrichment
            {
                CardId = card.Id,
                ComputedAt = computedAt,
                Status = verdict.Status,
                CardNumber = verdict.CardNumber,
                SetOfficialSize = verdict.SetOfficialSize,
                TcgdexSetId = verdict.TcgdexSetId,
                TcgdexCardId = verdict.TcgdexCardId,
                TcgdexName = verdict.TcgdexName,
                TcgdexVersion = manifest.Version,
            });
        }

        foreach (var chunk in inserts.Chunk(InsertChunk))
        {
            db.TcgdexEnrichments.AddRange(chunk);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        logger.LogInformation(
            "Enrichment sweep over {Cards} cards against TCGdex {Version}: {Written} verdicts written; "
            + "confirmed {Confirmed}, name-mismatch {NameMismatch}, number-not-found {NumberNotFound}, "
            + "ambiguous {Ambiguous}, no-number {NoNumber}, unmapped-set {UnmappedSet}",
            cards.Count,
            manifest.Version,
            inserts.Count,
            verdictCounts.GetValueOrDefault(TcgdexMatchStatus.Confirmed),
            verdictCounts.GetValueOrDefault(TcgdexMatchStatus.NameMismatch),
            verdictCounts.GetValueOrDefault(TcgdexMatchStatus.NumberNotFound),
            verdictCounts.GetValueOrDefault(TcgdexMatchStatus.Ambiguous),
            verdictCounts.GetValueOrDefault(TcgdexMatchStatus.NoNumber),
            verdictCounts.GetValueOrDefault(TcgdexMatchStatus.UnmappedSet));

        return new EnrichmentSweepResult
        {
            Version = manifest.Version,
            Cards = cards.Count,
            RowsWritten = inserts.Count,
            Verdicts = verdictCounts,
        };
    }

    /// <summary>Latest stored verdict per card, projected to the pure record
    /// so change-only comparison is plain record equality. Ordered read,
    /// last row per card wins — no per-group subquery for the provider to
    /// mistranslate.</summary>
    private static async Task<Dictionary<long, EnrichmentVerdict>> LoadLatestVerdictsAsync(
        PokemonDbContext db, CancellationToken ct)
    {
        var latest = new Dictionary<long, EnrichmentVerdict>();
        var rows = await db.TcgdexEnrichments
            .OrderBy(e => e.CardId).ThenBy(e => e.ComputedAt)
            .Select(e => new
            {
                e.CardId,
                e.Status,
                e.CardNumber,
                e.SetOfficialSize,
                e.TcgdexSetId,
                e.TcgdexCardId,
                e.TcgdexName,
            })
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            latest[row.CardId] = new EnrichmentVerdict
            {
                Status = row.Status,
                CardNumber = row.CardNumber,
                SetOfficialSize = row.SetOfficialSize,
                TcgdexSetId = row.TcgdexSetId,
                TcgdexCardId = row.TcgdexCardId,
                TcgdexName = row.TcgdexName,
            };
        }

        return latest;
    }
}
