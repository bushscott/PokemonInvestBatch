using System.Text;
using PokemonInvestBatch.Application.Enrichment;
using PokemonInvestBatch.Infrastructure.Enrichment;
using Xunit.Abstractions;

namespace PokemonInvestBatch.Integration.Tests;

/// <summary>
/// The measured-coverage audit behind ADR-0009's numbers: the real join —
/// SetMapper, TcgdexMatcher, the repo's alias file — run over a production
/// card export against a real TCGdex mirror, with no database and no writes.
/// Opt-in via TCGDEX_AUDIT_DIR because it wants a ~92k-card export beside it
/// (and will fetch a live mirror once if the directory lacks one), which CI
/// neither has nor should reach for.
///
/// Reproduce: export from the Pi (read-only) —
///   ssh scott@&lt;pi-ip&gt; "sudo -u postgres psql -d pokemon -Atc \"copy (
///     select c.id, c.name, s.slug, s.name from cards c join sets s on s.id = c.set_id
///     where c.not_a_card_at is null) to stdout with (format csv)\"" &gt; cards.csv
/// then TCGDEX_AUDIT_DIR=&lt;dir with cards.csv&gt; dotnet test --filter TcgdexCoverageAudit.
/// The report lands in &lt;dir&gt;/audit-report.txt.
/// </summary>
public class TcgdexCoverageAuditTests(ITestOutputHelper output)
{
    private static string? AuditDirectory => Environment.GetEnvironmentVariable("TCGDEX_AUDIT_DIR");

    [SkippableFact]
    public async Task Coverage_audit_over_a_production_export()
    {
        Skip.If(AuditDirectory is null, "TCGDEX_AUDIT_DIR not set (needs cards.csv; audit is opt-in).");
        var directory = AuditDirectory!;
        var cardsPath = Path.Combine(directory, "cards.csv");
        Skip.If(!File.Exists(cardsPath), $"{cardsPath} not found.");

        var mirrorDirectory = Path.Combine(directory, "mirror");
        if (!TcgdexMirror.Exists(mirrorDirectory))
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("PokemonInvestBatch-coverage-audit/1.0");
            await TcgdexMirror.FetchAsync(
                http, "https://api.tcgdex.net", mirrorDirectory, TimeProvider.System, CancellationToken.None);
        }

        var (catalog, manifest) = await TcgdexMirror.LoadAsync(mirrorDirectory, CancellationToken.None);

        var aliasPath = Path.Combine(directory, "tcgdex-set-aliases.json");
        var aliases = File.Exists(aliasPath)
            ? TcgdexSetAliases.Parse(await File.ReadAllTextAsync(aliasPath))
            : new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        var cards = new List<(long Id, string Name, string SetSlug, string SetName)>();
        foreach (var line in await File.ReadAllLinesAsync(cardsPath))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var fields = SplitCsv(line);
            cards.Add((long.Parse(fields[0]), fields[1], fields[2], fields[3]));
        }

        var map = SetMapper.Resolve(
            cards.Select(c => (c.SetSlug, c.SetName)).Distinct(),
            catalog,
            aliases);

        var byPartition = new SortedDictionary<string, Dictionary<TcgdexMatchStatus, int>>(StringComparer.Ordinal);
        var mismatches = new List<string>();
        var notFoundBySet = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var card in cards)
        {
            var entry = map[card.SetSlug];
            var verdict = TcgdexMatcher.Match(card.Name, entry, catalog);
            var partition = entry.Partition.ToString();
            if (!byPartition.TryGetValue(partition, out var counts))
            {
                byPartition[partition] = counts = [];
            }

            counts[verdict.Status] = counts.GetValueOrDefault(verdict.Status) + 1;

            if (verdict.Status == TcgdexMatchStatus.NameMismatch && mismatches.Count < 50)
            {
                mismatches.Add($"{card.Id}  {card.SetSlug}  '{card.Name}' vs tcgdex '{verdict.TcgdexName}' ({verdict.TcgdexCardId})");
            }

            if (verdict.Status == TcgdexMatchStatus.NumberNotFound)
            {
                notFoundBySet[card.SetSlug] = notFoundBySet.GetValueOrDefault(card.SetSlug) + 1;
            }
        }

        var report = new StringBuilder();
        report.AppendLine($"TCGdex coverage audit — {cards.Count} cards (not-a-card excluded), mirror {manifest.Version}");
        report.AppendLine();
        report.AppendLine("partition        confirmed  name-mism  num-not-found  ambiguous  no-number  unmapped-set  total");
        foreach (var (partition, counts) in byPartition)
        {
            var total = counts.Values.Sum();
            report.AppendLine(
                $"{partition,-15}{Count(counts, TcgdexMatchStatus.Confirmed),10}" +
                $"{Count(counts, TcgdexMatchStatus.NameMismatch),11}" +
                $"{Count(counts, TcgdexMatchStatus.NumberNotFound),15}" +
                $"{Count(counts, TcgdexMatchStatus.Ambiguous),11}" +
                $"{Count(counts, TcgdexMatchStatus.NoNumber),11}" +
                $"{Count(counts, TcgdexMatchStatus.UnmappedSet),14}{total,7}");
        }

        var all = byPartition.Values.SelectMany(c => c).GroupBy(kv => kv.Key)
            .ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));
        var grandTotal = all.Values.Sum();
        var confirmed = all.GetValueOrDefault(TcgdexMatchStatus.Confirmed);
        report.AppendLine();
        report.AppendLine($"confirmed {confirmed} of {grandTotal} ({100.0 * confirmed / grandTotal:F1}% of all cards)");
        var english = byPartition.GetValueOrDefault(nameof(SetPartition.English));
        if (english is not null)
        {
            var englishNumberable = english.Values.Sum() - Count(english, TcgdexMatchStatus.NoNumber);
            report.AppendLine(
                $"confirmed {Count(english, TcgdexMatchStatus.Confirmed)} of {englishNumberable} numbered English cards " +
                $"({100.0 * Count(english, TcgdexMatchStatus.Confirmed) / englishNumberable:F1}%)");
        }

        report.AppendLine();
        report.AppendLine("top number-not-found sets (coverage lag / renumbered products):");
        foreach (var (slug, count) in notFoundBySet.OrderByDescending(kv => kv.Value).Take(20))
        {
            report.AppendLine($"  {count,6}  {slug}");
        }

        report.AppendLine();
        report.AppendLine("first name-mismatches (review queue sample):");
        foreach (var mismatch in mismatches)
        {
            report.AppendLine($"  {mismatch}");
        }

        var text = report.ToString();
        output.WriteLine(text);
        await File.WriteAllTextAsync(Path.Combine(directory, "audit-report.txt"), text);
    }

    private static int Count(Dictionary<TcgdexMatchStatus, int> counts, TcgdexMatchStatus status) =>
        counts.GetValueOrDefault(status);

    /// <summary>Minimal RFC-4180 field splitter — card names carry commas
    /// and quotes ("Escape Rope", sealed lot titles), and psql quotes them.</summary>
    private static string[] SplitCsv(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return [.. fields];
    }
}
