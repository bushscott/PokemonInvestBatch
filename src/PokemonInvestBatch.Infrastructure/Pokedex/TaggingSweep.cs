using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Application.Pokedex;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Infrastructure.Pokedex;

/// <summary>Counts from one <see cref="TaggingSweep.RunAsync"/> call.
/// <c>Tagged + NoSpecies + Quarantined</c> equals <c>Examined</c> — every
/// examined card lands in exactly one status.</summary>
public sealed record TaggingSweepResult(int Examined, int Tagged, int NoSpecies, int Quarantined, int LinksWritten, int LinksRemoved);

/// <summary>
/// Re-tags every card whose species links may be stale (ADR-0011 item 3):
/// cards with no <c>card_tagging</c> row yet, and cards whose stored
/// <c>tagged_name</c> no longer matches <c>cards.name</c> — a title
/// correction, a re-crawl that picked up a rename. Every other card is left
/// alone, which is what makes <see cref="TaggingSweepResult.Examined"/> read
/// zero on a sweep over unchanged data: the work-set filter runs before any
/// matching happens, not after, so an unchanged card is never even handed
/// to <see cref="SpeciesMatcher.Match"/>.
///
/// Two exclusions the work-set filter enforces:
/// <list type="bullet">
/// <item><description><c>not_a_card_at</c> cards are never taggable — they
/// are consoles, games, and accessories the catalog mis-filed, not
/// Pokémon.</description></item>
/// <item><description>A card whose <c>card_tagging.method</c> is
/// <see cref="TagMethod.Manual"/> is skipped even when its name has changed
/// — the Pokédex phase's R10 ruling: an operator's pin freezes the card
/// until a human statement (never a sweep) unpins it.</description></item>
/// </list>
///
/// Per card examined, <see cref="SpeciesMatcher.Match"/> decides the
/// verdict. <c>card_tagging</c> is upserted — inserted on a card's first
/// attempt, updated in place otherwise — always with
/// <see cref="TagMethod.TitleMatch"/>, the only method this sweep ever
/// writes. <c>card_species</c> is diffed against the verdict: missing
/// <see cref="TagMethod.TitleMatch"/> links are inserted, stale ones are
/// deleted. A <see cref="TagMethod.Manual"/> link is invisible to the
/// removal side of that diff (never a deletion candidate) and blocks the
/// insertion side too — <c>(card_id, species_id)</c> is card_species' whole
/// primary key, so a machine link can never occupy a slot a Manual row
/// already holds. A <see cref="TagStatus.Quarantined"/> verdict's candidate
/// ids are recorded nowhere by this sweep (R5): card_tagging has no column
/// for them, and the empty desired-link set below is what deletes any
/// stale links a since-corrected title left behind.
/// </summary>
public sealed class TaggingSweep
{
    /// <summary>Cards handled per <c>SaveChanges</c>, matching
    /// <c>EnrichmentLane.InsertChunk</c> — this sweep runs over the same
    /// ~91k-card corpus, and clearing the change tracker between chunks
    /// keeps a full sweep from holding it all in memory at once.</summary>
    private const int ChunkSize = 2000;

    public async Task<TaggingSweepResult> RunAsync(
        PokemonDbContext db,
        IReadOnlyList<(string Name, int SpeciesId)> candidates,
        TimeProvider time,
        CancellationToken ct)
    {
        // LEFT JOIN on card_tagging's primary key (card_id), not two
        // full-table loads: a card qualifies with no tagging row at all, or
        // a non-Manual row whose tagged_name has drifted from the card's
        // current name.
        var workSet = await (
            from card in db.Cards
            join tagging in db.CardTagging on card.Id equals tagging.CardId into tags
            from tag in tags.DefaultIfEmpty()
            where card.NotACardAt == null
               && (tag == null || (tag.Method != TagMethod.Manual && tag.TaggedName != card.Name))
            select new { card.Id, card.Name })
            .ToListAsync(ct);

        var now = time.GetUtcNow();
        var tagged = 0;
        var noSpecies = 0;
        var quarantined = 0;
        var linksWritten = 0;
        var linksRemoved = 0;

        foreach (var chunk in workSet.Chunk(ChunkSize))
        {
            var chunkIds = chunk.Select(c => c.Id).ToList();

            // Tracked (no AsNoTracking): an existing row is updated in
            // place, so SaveChanges needs the original values to diff
            // against.
            var existingTagging = await db.CardTagging
                .Where(t => chunkIds.Contains(t.CardId))
                .ToDictionaryAsync(t => t.CardId, ct);

            // Every method, not just TitleMatch: a Manual link occupies the
            // same (card_id, species_id) primary key a machine link would
            // otherwise claim, so the "already present" check below must
            // see it even though it is never a removal candidate. Untracked
            // — links are only ever added or removed wholesale, never
            // mutated in place.
            var existingLinksByCard = (await db.CardSpecies
                    .Where(l => chunkIds.Contains(l.CardId))
                    .AsNoTracking()
                    .ToListAsync(ct))
                .ToLookup(l => l.CardId);

            foreach (var card in chunk)
            {
                var verdict = SpeciesMatcher.Match(card.Name, candidates);
                switch (verdict.Status)
                {
                    case TagStatus.Tagged:
                        tagged++;
                        break;
                    case TagStatus.NoSpecies:
                        noSpecies++;
                        break;
                    case TagStatus.Quarantined:
                        quarantined++;
                        break;
                }

                if (existingTagging.TryGetValue(card.Id, out var row))
                {
                    row.Status = verdict.Status;
                    row.Method = TagMethod.TitleMatch;
                    row.TaggedName = card.Name;
                    row.UpdatedAt = now;
                }
                else
                {
                    db.CardTagging.Add(new CardTagging
                    {
                        CardId = card.Id,
                        Status = verdict.Status,
                        Method = TagMethod.TitleMatch,
                        TaggedName = card.Name,
                        UpdatedAt = now,
                    });
                }

                var manualIds = new HashSet<int>();
                var titleMatchLinks = new List<CardSpeciesLink>();
                foreach (var link in existingLinksByCard[card.Id])
                {
                    if (link.Method == TagMethod.Manual)
                    {
                        manualIds.Add(link.SpeciesId);
                    }
                    else
                    {
                        titleMatchLinks.Add(link);
                    }
                }

                // Only a Tagged verdict wants any links — NoSpecies and
                // Quarantined both want zero, which is what deletes stale
                // TitleMatch links a since-corrected title left behind.
                var desiredIds = verdict.Status == TagStatus.Tagged
                    ? new HashSet<int>(verdict.SpeciesIds)
                    : new HashSet<int>();
                var titleMatchIds = new HashSet<int>(titleMatchLinks.Select(l => l.SpeciesId));

                foreach (var speciesId in desiredIds)
                {
                    if (manualIds.Contains(speciesId) || titleMatchIds.Contains(speciesId))
                    {
                        continue;
                    }

                    db.CardSpecies.Add(new CardSpeciesLink
                    {
                        CardId = card.Id,
                        SpeciesId = speciesId,
                        Method = TagMethod.TitleMatch,
                    });
                    linksWritten++;
                }

                foreach (var link in titleMatchLinks)
                {
                    if (!desiredIds.Contains(link.SpeciesId))
                    {
                        db.CardSpecies.Remove(link);
                        linksRemoved++;
                    }
                }
            }

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        return new TaggingSweepResult(workSet.Count, tagged, noSpecies, quarantined, linksWritten, linksRemoved);
    }
}
