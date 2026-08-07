using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Application.Alerting;
using PokemonInvestBatch.Domain.Parsing;

namespace PokemonInvestBatch.Infrastructure.Persistence;

/// <summary>
/// The site's changelog, kept by us because the site does not publish one.
///
/// Every card page is fingerprinted down to its structure, and a fingerprint
/// never seen before is the site telling us it changed. The HTML that produced
/// it is written to disk at that moment — by the time anyone investigates, the
/// live page has usually moved on, so the sample is the only evidence of what
/// the parser actually saw.
///
/// A first sighting is worth one alert and no more: a markup change lands on
/// every card at once, and a thousand identical emails is the same information
/// as one.
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

        if (throttle.ShouldAlert($"new-page-shape:{print.Hash}", now))
        {
            await alerter.RaiseAsync(
                "New page shape observed",
                $"Card detail pages have a structure never seen before.\nSample: {cardUrl}\n"
                + $"Shape: {print.ShapeJson}\nArchived: {archivePath}",
                ct);
        }

        return print.Hash;
    }
}
