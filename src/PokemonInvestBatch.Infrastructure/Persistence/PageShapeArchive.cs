using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Application.Alerting;
using PokemonInvestBatch.Domain.Parsing;

namespace PokemonInvestBatch.Infrastructure.Persistence;

/// <summary>
/// The site's changelog, kept by us because the site does not publish one.
///
/// Every card page is fingerprinted down to its structure, and a fingerprint
/// never seen before is archived — HTML and all — because by the time anyone
/// investigates, the live page has usually moved on, so the sample is the only
/// evidence of what the parser actually saw.
///
/// Archiving is not alerting. A shape counts how much data a card carries as
/// well as how the page is built, so an obscure promo with one price tier and
/// no census is structurally novel and perfectly healthy; alerting on every
/// such card buried the channel under ten Criticals in half an hour on
/// 2026-08-07. Only an unfamiliar <em>name</em> is the site moving, and only
/// that raises an alert — once, no matter how many pages carry it, because a
/// markup change lands on every card at once and a thousand identical emails
/// is the same information as one.
/// </summary>
public sealed class PageShapeArchive(
    IncidentThrottle throttle,
    IAlerter alerter,
    string archiveDirectory)
{
    /// <summary>Records the page's shape and returns its hash. The caller
    /// saves — the row rides the same transaction as the visit it describes.</summary>
    public async Task<string> RecordAsync(
        PokemonDbContext db, string cardUrl, string html, DateTimeOffset now, CancellationToken ct)
    {
        var print = PageFingerprint.OfCardDetailPage(html);
        var known = await db.Shapes.FindAsync([print.Hash], ct);
        if (known is not null)
        {
            known.LastSeenAt = now;
            return print.Hash;
        }

        // Read the vocabulary before this shape joins it, or every name it
        // brings is its own precedent and nothing is ever unfamiliar.
        var vocabulary = await db.Shapes.Select(s => s.ShapeJson).ToListAsync(ct);
        var unfamiliar = PageShapeVocabulary.NamesAbsentFrom(print.ShapeJson, vocabulary);

        db.Shapes.Add(new PageShape
        {
            Hash = print.Hash,
            ShapeJson = print.ShapeJson,
            SampleUrl = cardUrl,
            FirstSeenAt = now,
            LastSeenAt = now,
        });

        Directory.CreateDirectory(archiveDirectory);
        var archivePath = Path.Combine(archiveDirectory, $"{print.Hash}.html");
        await File.WriteAllTextAsync(archivePath, html, ct);

        // An empty archive has nothing to be unfamiliar against: on a first
        // run every name is new, which is the thousand-identical-emails case
        // rather than news.
        if (vocabulary.Count == 0 || unfamiliar.Count == 0)
        {
            return print.Hash;
        }

        // Keyed on the names, not the hash: one new tier reaches us through
        // however many shapes, and they are all the same piece of news.
        if (throttle.ShouldAlert($"new-page-element:{string.Join(",", unfamiliar)}", now))
        {
            await alerter.RaiseAsync(
                "New page element observed",
                $"Card detail pages carry a name we have never seen.\nSample: {cardUrl}\n"
                + $"New: {string.Join(", ", unfamiliar)}\n"
                + $"Shape: {print.ShapeJson}\nArchived: {archivePath}",
                ct);
        }

        return print.Hash;
    }
}
