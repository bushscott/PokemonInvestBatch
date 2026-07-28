using PokemonInvestBatch.Domain.Parsing;

namespace PokemonInvestBatch.Domain.Tests.Parsing;

public class PopulationRestatementTests
{
    private static PopulationReport Report(int[] psa, int[]? cgc = null) =>
        new() { Psa = psa, Cgc = cgc ?? new int[10] };

    private static int[] Grades(short grade, int count)
    {
        var grades = new int[10];
        grades[grade - 1] = count;
        return grades;
    }

    [Fact]
    public void The_june_2026_psa_restatement_is_a_spike()
    {
        // Charizard PSA 10: 397 → 99,246 overnight. That is PSA changing how
        // it counts, not 98,849 cards getting graded in a month.
        var report = Report(Grades(grade: 10, count: 99_246));
        var lastKnown = new Dictionary<(string, short), int> { [("psa", 10)] = 397 };

        var anomaly = Assert.Single(PopulationRestatement.Anomalies(report, lastKnown));
        Assert.Equal("psa", anomaly.Grader);
        Assert.Equal(10, anomaly.Grade);
        Assert.Equal(397, anomaly.Previous);
        Assert.Equal(99_246, anomaly.Current);
        Assert.Equal(PopulationAnomalyKind.Spike, anomaly.Kind);
    }

    [Fact]
    public void Organic_monthly_growth_is_not_an_anomaly()
    {
        var report = Report(Grades(grade: 10, count: 450));
        var lastKnown = new Dictionary<(string, short), int> { [("psa", 10)] = 397 };

        Assert.Empty(PopulationRestatement.Anomalies(report, lastKnown));
    }

    [Fact]
    public void A_small_base_can_multiply_without_alarm()
    {
        // 3 → 40 is a hot new card getting graded, not a census restatement.
        // The ratio test only applies once a cell is established.
        var report = Report(Grades(grade: 10, count: 40));
        var lastKnown = new Dictionary<(string, short), int> { [("psa", 10)] = 3 };

        Assert.Empty(PopulationRestatement.Anomalies(report, lastKnown));
    }

    [Fact]
    public void First_observation_is_never_an_anomaly()
    {
        // Backfilling a never-visited card jumps every cell from implicit
        // zero to its full census — that is discovery, not restatement.
        var report = Report(Grades(grade: 10, count: 99_246));

        Assert.Empty(PopulationRestatement.Anomalies(
            report, new Dictionary<(string, short), int>()));
    }

    [Fact]
    public void A_shrinking_population_is_an_anomaly()
    {
        // Graded cards do not become ungraded; a census can only grow.
        var report = Report(Grades(grade: 9, count: 80));
        var lastKnown = new Dictionary<(string, short), int> { [("psa", 9)] = 100 };

        var anomaly = Assert.Single(PopulationRestatement.Anomalies(report, lastKnown));
        Assert.Equal(PopulationAnomalyKind.Decrease, anomaly.Kind);
        Assert.Equal(100, anomaly.Previous);
        Assert.Equal(80, anomaly.Current);
    }

    [Fact]
    public void Cgc_cells_are_checked_too()
    {
        var report = Report(new int[10], Grades(grade: 8, count: 5_000));
        var lastKnown = new Dictionary<(string, short), int> { [("cgc", 8)] = 120 };

        var anomaly = Assert.Single(PopulationRestatement.Anomalies(report, lastKnown));
        Assert.Equal("cgc", anomaly.Grader);
        Assert.Equal(PopulationAnomalyKind.Spike, anomaly.Kind);
    }

    [Fact]
    public void An_unchanged_census_is_silent()
    {
        var report = Report(Grades(grade: 10, count: 397));
        var lastKnown = new Dictionary<(string, short), int> { [("psa", 10)] = 397 };

        Assert.Empty(PopulationRestatement.Anomalies(report, lastKnown));
    }
}
