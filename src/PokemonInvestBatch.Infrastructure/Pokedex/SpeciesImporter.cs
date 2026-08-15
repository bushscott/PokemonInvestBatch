using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Application.Pokedex;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Infrastructure.Pokedex;

/// <summary>Upsert counts from one <see cref="SpeciesImporter.ImportAsync"/>
/// call. Always reconciles: <c>Inserted + Updated + Unchanged</c> equals the
/// number of species handed in.</summary>
public sealed record SpeciesImportResult
{
    /// <summary>Species with no prior row — written along with every one of
    /// its type, egg-group, and localized-name rows.</summary>
    public required int Inserted { get; init; }

    /// <summary>Species with a prior row where at least one scalar column or
    /// child collection (types, egg groups, or localized names) differed
    /// from the dataset. The row's scalars are overwritten in place and its
    /// child rows are fully replaced — deleted, then reinserted from the
    /// dataset — never diffed row-by-row.</summary>
    public required int Updated { get; init; }

    /// <summary>Species with a prior row that already matched the dataset in
    /// every scalar column and every child collection. Left completely
    /// untouched: no property assignment on the row, no delete or insert on
    /// its children. This is what makes calling
    /// <see cref="SpeciesImporter.ImportAsync"/> with the full ~1,025-species
    /// dataset every sweep cheap in steady state — the common case, where
    /// almost nothing changed since the last run, costs no writes at
    /// all.</summary>
    public required int Unchanged { get; init; }
}

/// <summary>
/// Upserts a PokéAPI dataset (<see cref="PokeapiDataset.Load"/>'s output)
/// into the four species tables (ADR-0011): <c>species</c>,
/// <c>species_types</c>, <c>species_egg_groups</c>, <c>species_names</c>.
/// Meant to be called every sweep with the full dataset — load-all-compare-
/// write, no chunking: at ~1,025 species this comfortably fits in memory, so
/// every existing species row and every existing child row is read once,
/// compared against the incoming dataset, and only what actually differs is
/// written.
///
/// Comparison is keyed by dex number (<see cref="SpeciesImport.Id"/> /
/// <see cref="Species.Id"/>) against every scalar column and every child
/// collection — types (order-sensitive: PK includes slot), egg groups and
/// localized names (both order-insensitive: no slot column, so they compare
/// as sets/maps). A species counts as
/// <see cref="SpeciesImportResult.Unchanged"/> only when nothing at all
/// differs; a single difference anywhere makes it
/// <see cref="SpeciesImportResult.Updated"/>, with its child rows fully
/// replaced rather than diffed row-by-row — ADR-0011 §6 states this
/// explicitly ("a changed species' children are replaced wholesale on
/// re-import rather than diffed row by row"), and it is also what keeps
/// "replaced, never duplicated" true regardless of which child rows actually
/// changed.
///
/// <c>species.evolves_from_species_id</c> is a real self-referencing foreign
/// key (Restrict; see <c>PokemonDbContext.OnModelCreating</c>), and PokéAPI's
/// dex order is not parent-first — Pikachu (25) evolves from Pichu (172), a
/// higher dex number, so a species can appear in the dataset before the row
/// its own foreign key points at. This importer does not sort the input to
/// work around that: every add/remove for the whole batch lands in the same
/// change tracker and is flushed with a single <c>SaveChangesAsync</c> call,
/// and EF Core topologically sorts the INSERT statements it generates by the
/// model's foreign-key graph — not by the order entities were added to the
/// context — so a parent row is always written before a child that
/// references it, regardless of dex order.
///
/// The whole call — every scalar upsert and every child-row replacement —
/// runs inside one transaction: a species importer half-applied by a
/// mid-run failure would leave some rows on the new dataset and others on
/// the old one, and nothing downstream could tell.
/// </summary>
public static class SpeciesImporter
{
    public static async Task<SpeciesImportResult> ImportAsync(
        PokemonDbContext db, IReadOnlyList<SpeciesImport> species, CancellationToken ct)
    {
        // Tracked: an Update needs to mutate these in place so SaveChanges
        // emits columns that actually changed.
        var existingSpecies = await db.SpeciesRows.ToDictionaryAsync(s => s.Id, ct);

        // AsNoTracking: read-only inputs to the comparison below. A changed
        // species' rows are handed straight to RemoveRange, which attaches
        // and marks them Deleted from their key values alone — the
        // untracked read costs nothing extra for that.
        var typesBySpecies = (await db.SpeciesTypes.AsNoTracking().ToListAsync(ct)).ToLookup(t => t.SpeciesId);
        var eggGroupsBySpecies = (await db.SpeciesEggGroups.AsNoTracking().ToListAsync(ct)).ToLookup(e => e.SpeciesId);
        var namesBySpecies = (await db.SpeciesNames.AsNoTracking().ToListAsync(ct)).ToLookup(n => n.SpeciesId);

        var inserted = 0;
        var updated = 0;
        var unchanged = 0;

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        foreach (var import in species)
        {
            if (existingSpecies.TryGetValue(import.Id, out var row))
            {
                var matches = ScalarsMatch(row, import)
                    && TypesMatch(import.Types, typesBySpecies[import.Id])
                    && EggGroupsMatch(import.EggGroups, eggGroupsBySpecies[import.Id])
                    && NamesMatch(import.LocalizedNames, namesBySpecies[import.Id]);

                if (matches)
                {
                    unchanged++;
                    continue;
                }

                ApplyScalars(row, import);
                updated++;
            }
            else
            {
                db.SpeciesRows.Add(BuildSpecies(import));
                inserted++;
            }

            // Full replace, not a row-by-row diff (ADR-0011 §6). The
            // RemoveRange calls are no-ops for a brand-new species — the
            // lookups have nothing under an id that was never inserted.
            db.SpeciesTypes.RemoveRange(typesBySpecies[import.Id]);
            db.SpeciesEggGroups.RemoveRange(eggGroupsBySpecies[import.Id]);
            db.SpeciesNames.RemoveRange(namesBySpecies[import.Id]);
            AddChildren(db, import);
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new SpeciesImportResult { Inserted = inserted, Updated = updated, Unchanged = unchanged };
    }

    private static bool ScalarsMatch(Species row, SpeciesImport import) =>
        string.Equals(row.Name, import.Name, StringComparison.Ordinal)
        && string.Equals(row.Slug, import.Slug, StringComparison.Ordinal)
        && row.Generation == import.Generation
        && string.Equals(row.Region, import.Region, StringComparison.Ordinal)
        && string.Equals(row.Color, import.Color, StringComparison.Ordinal)
        && string.Equals(row.Habitat, import.Habitat, StringComparison.Ordinal)
        && row.Status == import.Status
        && row.Stage == import.Stage
        && row.EvolvesFromSpeciesId == import.EvolvesFrom
        && string.Equals(row.GradientStart, import.GradientStart, StringComparison.Ordinal)
        && string.Equals(row.GradientEnd, import.GradientEnd, StringComparison.Ordinal);

    private static void ApplyScalars(Species row, SpeciesImport import)
    {
        row.Name = import.Name;
        row.Slug = import.Slug;
        row.Generation = import.Generation;
        row.Region = import.Region;
        row.Color = import.Color;
        row.Habitat = import.Habitat;
        row.Status = import.Status;
        row.Stage = import.Stage;
        row.EvolvesFromSpeciesId = import.EvolvesFrom;
        row.GradientStart = import.GradientStart;
        row.GradientEnd = import.GradientEnd;
    }

    private static Species BuildSpecies(SpeciesImport import) => new()
    {
        Id = import.Id,
        Name = import.Name,
        Slug = import.Slug,
        Generation = import.Generation,
        Region = import.Region,
        Color = import.Color,
        Habitat = import.Habitat,
        Status = import.Status,
        Stage = import.Stage,
        EvolvesFromSpeciesId = import.EvolvesFrom,
        GradientStart = import.GradientStart,
        GradientEnd = import.GradientEnd,
    };

    /// <summary>Order-sensitive: <c>species_types</c>' key includes slot, so
    /// the same two types in a different order is a real change, not a
    /// no-op.</summary>
    private static bool TypesMatch(IReadOnlyList<string> importTypes, IEnumerable<SpeciesType> existingTypes) =>
        importTypes.SequenceEqual(existingTypes.OrderBy(t => t.Slot).Select(t => t.Type), StringComparer.Ordinal);

    /// <summary>Order-insensitive: <c>species_egg_groups</c> carries no slot
    /// column, so Postgres makes no ordering promise for it and two reads of
    /// the same set could come back in different orders.</summary>
    private static bool EggGroupsMatch(IReadOnlyList<string> importGroups, IEnumerable<SpeciesEggGroup> existingGroups) =>
        new HashSet<string>(importGroups, StringComparer.Ordinal).SetEquals(existingGroups.Select(e => e.EggGroup));

    private static bool NamesMatch(IReadOnlyDictionary<string, string> importNames, IEnumerable<SpeciesName> existingNames)
    {
        var existing = existingNames.ToDictionary(n => n.Language, n => n.Name, StringComparer.Ordinal);
        return existing.Count == importNames.Count
            && importNames.All(pair =>
                existing.TryGetValue(pair.Key, out var name) && string.Equals(name, pair.Value, StringComparison.Ordinal));
    }

    private static void AddChildren(PokemonDbContext db, SpeciesImport import)
    {
        db.SpeciesTypes.AddRange(import.Types.Select((type, index) => new SpeciesType
        {
            SpeciesId = import.Id,
            Slot = (short)(index + 1),
            Type = type,
        }));

        db.SpeciesEggGroups.AddRange(import.EggGroups.Select(group => new SpeciesEggGroup
        {
            SpeciesId = import.Id,
            EggGroup = group,
        }));

        db.SpeciesNames.AddRange(import.LocalizedNames.Select(pair => new SpeciesName
        {
            SpeciesId = import.Id,
            Language = pair.Key,
            Name = pair.Value,
        }));
    }
}
