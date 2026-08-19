using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Application.Enrichment;
using PokemonInvestBatch.Application.Pokedex;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Infrastructure.Pokedex;

/// <summary>Counts from one <see cref="SetDetailsSweep.RunAsync"/> call.
/// <c>Matched + Pending</c> equals the number of rows in <c>sets</c> — every
/// set gets exactly one <c>set_details</c> row, always. <c>Partitions</c>
/// splits the same totals by language shelf, which is what the receipt log
/// prints so a curation session can see which shelf's pending count moved.</summary>
public sealed record SetDetailsSweepResult(
    int Matched,
    int Pending,
    IReadOnlyDictionary<SetPartition, (int Matched, int Pending)> Partitions);

/// <summary>
/// Fills <c>set_details</c> from the existing TCGdex set map (ADR-0009,
/// reused rather than duplicated): one row per <c>sets</c> row, always. A
/// set <see cref="SetMapper.Resolve"/> resolves to <see cref="SetMapKind.Mapped"/>
/// becomes <see cref="SetMatchStatus.Matched"/> with its TCGdex code,
/// release date and serie — a Japanese entry's targets read from the ja
/// shelf's own catalog (ADR-0012), everything else from the English one,
/// the same partition-scoping the resolver used; everything else — no
/// TCGdex counterpart
/// (<see cref="SetMapKind.Unmapped"/>), or the promo grab-bag slug that
/// fans out per-card by number prefix rather than naming one set
/// (<see cref="SetMapKind.PromoPool"/>) — is <see cref="SetMatchStatus.Pending"/>
/// with every field null.
///
/// A trainer-kit alias names two TCGdex half-deck sets for one
/// PriceCharting set; this sweep records the first target's code, release
/// date and serie. Verified live against api.tcgdex.net 2026-08-15: every
/// half-deck pair shares one physical release, so date and serie are
/// identical either way and only <c>code</c> is a real simplification —
/// recording one half of a two-set alias rather than inventing a combined
/// code TCGdex does not itself publish.
///
/// Idempotent by construction, not by an explicit unchanged-skip: every set
/// is read into a tracked row and every field is reassigned every run, but
/// EF Core's change tracker only emits an UPDATE for a property whose value
/// actually differs from what is stored, so a re-run against unchanged
/// inputs (unchanged sets, mirror, aliases, and era file) writes nothing.
/// No chunking (unlike <see cref="TaggingSweep"/>): the corpus is one row
/// per set, roughly 800 today (ADR-0009), not per card.
/// </summary>
public sealed class SetDetailsSweep(
    TcgdexCatalog catalog,
    IReadOnlyDictionary<string, IReadOnlyList<string>> aliases,
    SetMapper.JapaneseShelf japanese,
    string seriesEraPath)
{
    public async Task<SetDetailsSweepResult> RunAsync(PokemonDbContext db, CancellationToken ct)
    {
        // Same posture as tcgdex-set-aliases.json (EnrichmentLane): absent
        // file means empty (every era resolves to null), malformed refuses
        // loudly. Read before anything else so a bad file leaves no partial
        // sweep behind — nothing below has touched the database yet.
        var eras = File.Exists(seriesEraPath)
            ? TcgdexSeriesEras.Parse(await File.ReadAllTextAsync(seriesEraPath, ct))
            : new Dictionary<string, string>(StringComparer.Ordinal);

        var sets = await db.Sets.Select(s => new { s.Id, s.Slug, s.Name }).ToListAsync(ct);
        var map = SetMapper.Resolve(sets.Select(s => (s.Slug, s.Name)), catalog, aliases, japanese);

        // Tracked: an existing row is updated in place rather than
        // replaced, which is what makes the idempotency claim above hold —
        // DetectChanges compares against these original values.
        var existing = await db.SetDetails.ToDictionaryAsync(d => d.SetId, ct);

        var matched = 0;
        var pending = 0;
        var partitions = new Dictionary<SetPartition, (int Matched, int Pending)>();

        foreach (var set in sets)
        {
            if (!existing.TryGetValue(set.Id, out var detail))
            {
                detail = new SetDetail { SetId = set.Id };
                db.SetDetails.Add(detail);
            }

            var entry = map[set.Slug];
            var tally = partitions.GetValueOrDefault(entry.Partition);
            if (entry.Kind == SetMapKind.Mapped)
            {
                // The first alias target when a PriceCharting set names more
                // than one TCGdex set (trainer-kit half-decks) — see the
                // class remarks for why that is a safe, documented choice
                // rather than an arbitrary one. A Japanese entry's targets
                // live in the ja catalog; everything else joined through the
                // English one — the same partition-scoping the resolver used.
                var shelf = entry.Partition == SetPartition.Japanese ? japanese.Catalog : catalog;
                var target = entry.TcgdexSetIds[0];
                var tcgdexSet = shelf.ById(target)
                    ?? throw new InvalidOperationException(
                        $"Set map for '{entry.Slug}' names TCGdex set '{target}', which the mirror does not contain.");

                detail.MatchStatus = SetMatchStatus.Matched;
                detail.Code = tcgdexSet.Id;
                detail.ReleasedOn = tcgdexSet.ReleaseDate;
                detail.Series = tcgdexSet.SerieName;
                detail.Era = eras.GetValueOrDefault(tcgdexSet.SerieName);
                matched++;
                partitions[entry.Partition] = (tally.Matched + 1, tally.Pending);
            }
            else
            {
                // Unmapped (no TCGdex counterpart) and the promo grab-bag
                // (never names exactly one TCGdex set) both read the same
                // honest "not yet matched" state.
                detail.MatchStatus = SetMatchStatus.Pending;
                detail.Code = null;
                detail.ReleasedOn = null;
                detail.Series = null;
                detail.Era = null;
                pending++;
                partitions[entry.Partition] = (tally.Matched, tally.Pending + 1);
            }
        }

        await db.SaveChangesAsync(ct);

        return new SetDetailsSweepResult(matched, pending, partitions);
    }
}
