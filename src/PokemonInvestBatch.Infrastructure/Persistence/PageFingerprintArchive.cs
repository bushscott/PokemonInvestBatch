using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Application.Alerting;
using PokemonInvestBatch.Domain.Parsing;

namespace PokemonInvestBatch.Infrastructure.Persistence;

/// <summary>
/// The site's changelog, kept by us because the site does not publish one.
///
/// Every card page is fingerprinted, and a fingerprint never seen before is
/// archived — HTML and all — because by the time anyone investigates, the live
/// page has usually moved on, so the sample is the only evidence of what the
/// parser actually saw.
///
/// Archiving is not alerting. A fingerprint counts how much data a card carries as
/// well as how the page is built, so an obscure promo with one price tier and
/// no census is a combination never seen and a card in perfect health; alerting
/// on every such card buried the channel under ten Criticals in half an hour on
/// 2026-08-07. Only an unfamiliar <em>name</em> is the site moving, and only
/// that raises an alert — once, no matter how many pages carry it, because a
/// markup change lands on every card at once and a thousand identical emails
/// is the same information as one.
/// </summary>
public sealed class PageFingerprintArchive(
    IncidentThrottle throttle,
    IAlerter alerter,
    string archiveDirectory)
{
    /// <summary>Records the page's fingerprint and returns its hash. The row
    /// commits on its own, immediately, outside whatever transaction the caller
    /// is building: it records that we <em>saw</em> this page shape, which is
    /// true whether or not the visit that saw it goes on to commit — the same
    /// footing the archived HTML has always stood on.</summary>
    public async Task<string> RecordAsync(
        PokemonDbContext db, string cardUrl, string html, DateTimeOffset now, CancellationToken ct)
    {
        var fingerprint = PageFingerprint.OfCardDetailPage(html);

        // Read the vocabulary before this fingerprint joins it, or every name it
        // brings is its own precedent and nothing is ever unfamiliar. Only worth
        // reading when the hash looks new; the upsert below has the final say.
        var known = await db.Fingerprints.FindAsync([fingerprint.Hash], ct);
        var vocabulary = known is null
            ? await db.Fingerprints.Select(f => f.Names).ToListAsync(ct)
            : [];

        if (!await ClaimAsync(db, fingerprint, cardUrl, now, ct))
        {
            // Someone else recorded this shape between our read and our write —
            // the lane and an express visit, or two express visits. Theirs is
            // the archive copy and theirs is the alert; ours would be a
            // duplicate of both.
            return fingerprint.Hash;
        }

        var unfamiliar = FingerprintVocabulary.NamesAbsentFrom(fingerprint.Names, vocabulary);

        Directory.CreateDirectory(archiveDirectory);
        var archivePath = Path.Combine(archiveDirectory, $"{fingerprint.Hash}.html");
        await File.WriteAllTextAsync(archivePath, html, ct);

        // An empty archive has nothing to be unfamiliar against: on a first
        // run every name is new, which is the thousand-identical-emails case
        // rather than news.
        if (vocabulary.Count == 0 || unfamiliar.Count == 0)
        {
            return fingerprint.Hash;
        }

        // Keyed on the names, not the hash: one new tier reaches us through
        // however many fingerprints, and they are all the same piece of news.
        if (throttle.ShouldAlert($"new-page-element:{string.Join(",", unfamiliar)}", now))
        {
            await alerter.RaiseAsync(
                "New page element observed",
                $"Card detail pages carry a name we have never seen.\nSample: {cardUrl}\n"
                + $"New: {string.Join(", ", unfamiliar)}\n"
                + $"Names: {fingerprint.Names}\nArchived: {archivePath}",
                ct);
        }

        return fingerprint.Hash;
    }

    /// <summary>
    /// Writes the row and answers "was it mine to write?" in one statement, so
    /// two visits meeting the same new shape at the same moment both succeed and
    /// exactly one of them archives it. The second raw-SQL path in the codebase,
    /// alongside <see cref="SaleWriter"/>, and for the same reason: dedup only
    /// the database can do without a race.
    ///
    /// <c>xmax = 0</c> is Postgres for "this row came from my INSERT rather than
    /// the conflicting UPDATE". The CTE keeps a plain SELECT at the top level,
    /// which is what EF can execute; <c>AS "Value"</c> is the column name it
    /// requires of a scalar query.
    /// </summary>
    private static async Task<bool> ClaimAsync(
        PokemonDbContext db, PageFingerprint fingerprint, string cardUrl, DateTimeOffset now, CancellationToken ct)
    {
        var claimed = await db.Database.SqlQuery<bool>($"""
            WITH upsert AS (
                INSERT INTO fingerprints (hash, names, sample_url, first_seen_at, last_seen_at)
                VALUES ({fingerprint.Hash}, {fingerprint.Names}::jsonb, {cardUrl}, {now}, {now})
                ON CONFLICT (hash) DO UPDATE SET last_seen_at = {now}
                RETURNING xmax = 0 AS inserted
            )
            SELECT inserted AS "Value" FROM upsert
            """).ToListAsync(ct);

        return claimed.Single();
    }
}
