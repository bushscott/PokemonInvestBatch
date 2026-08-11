namespace PokemonInvestBatch.Domain.Parsing;

public enum PopulationAnomalyKind
{
    /// <summary>An established cell multiplied past any plausible grading pace.</summary>
    Spike,

    /// <summary>A census shrank far enough that the grader must have recounted
    /// the cell, not merely lost a slab or two to cracking and regrading.</summary>
    Decrease,
}

/// <summary>A census cell whose change cannot be organic market activity.</summary>
public readonly record struct PopulationAnomaly(
    string Grader, short Grade, int Previous, int Current, PopulationAnomalyKind Kind);

/// <summary>
/// Value invariant (integrity layer 4): an impossible census change.
/// A cell grows at grading pace and shrinks only slowly: slabs do leave a
/// census by being cracked, crossed over, or regraded, so a drop of a card or
/// two is ordinary attrition and is deliberately not flagged. What cannot be
/// organic is a cell multiplying — PSA restated its census ~June 2026 and
/// Charizard PSA 10 went 397 → 99,246 overnight — or a fifth of a cell
/// vanishing at once. Both are the grader changing how it counts, not the
/// market, and must be flagged so downstream analytics never mistake a
/// restatement for demand.
/// </summary>
public static class PopulationRestatement
{
    /// <summary>A cell multiplying beyond this factor is a restatement.</summary>
    private const int SpikeFactor = 10;

    /// <summary>Cells below this base are still "discovery" — a hot new card
    /// can multiply a tiny census organically in one gap between visits.</summary>
    private const int SpikeFloor = 10;

    /// <summary>A shrink this fraction of the cell or larger is a recount.
    /// Observed routine attrition runs under a tenth of a cell.</summary>
    private const double DecreaseFraction = 0.20;

    /// <summary>One slab is never a recount, however small the cell — it was
    /// cracked, crossed over, or regraded, and percentages of a handful of
    /// cards mean nothing.</summary>
    private const int DecreaseFloor = 2;

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

                var lost = previous - current;
                if (lost >= DecreaseFloor && lost >= previous * DecreaseFraction)
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
