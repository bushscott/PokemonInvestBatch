namespace PokemonInvestBatch.Domain.Parsing;

public enum PopulationAnomalyKind
{
    /// <summary>An established cell multiplied past any plausible grading pace.</summary>
    Spike,

    /// <summary>A census shrank; graded cards do not become ungraded.</summary>
    Decrease,
}

/// <summary>A census cell whose change cannot be organic market activity.</summary>
public readonly record struct PopulationAnomaly(
    string Grader, short Grade, int Previous, int Current, PopulationAnomalyKind Kind);

/// <summary>
/// Value invariant (integrity layer 4): a graded population only grows, and
/// only at grading pace. PSA restated its census ~June 2026 (Charizard PSA 10
/// went 397 → 99,246 overnight) — such jumps are the source changing its
/// methodology, not the market, and must be flagged so downstream analytics
/// never mistake a restatement for demand.
/// </summary>
public static class PopulationRestatement
{
    /// <summary>A cell multiplying beyond this factor is a restatement.</summary>
    private const int SpikeFactor = 10;

    /// <summary>Cells below this base are still "discovery" — a hot new card
    /// can multiply a tiny census organically in one gap between visits.</summary>
    private const int SpikeFloor = 10;

    public static IReadOnlyList<PopulationAnomaly> Anomalies(
        PopulationReport population,
        IReadOnlyDictionary<(string Grader, short Grade), int> lastKnown)
    {
        var anomalies = new List<PopulationAnomaly>();
        foreach (var (grader, grades) in new[] { ("psa", population.Psa), ("cgc", population.Cgc) })
        {
            for (var i = 0; i < grades.Count; i++)
            {
                var grade = (short)(i + 1);
                var current = grades[i];
                var previous = lastKnown.GetValueOrDefault((grader, grade));

                if (current < previous)
                {
                    anomalies.Add(new PopulationAnomaly(grader, grade, previous, current, PopulationAnomalyKind.Decrease));
                }
                else if (previous >= SpikeFloor && current > previous * SpikeFactor)
                {
                    anomalies.Add(new PopulationAnomaly(grader, grade, previous, current, PopulationAnomalyKind.Spike));
                }
            }
        }

        return anomalies;
    }
}
