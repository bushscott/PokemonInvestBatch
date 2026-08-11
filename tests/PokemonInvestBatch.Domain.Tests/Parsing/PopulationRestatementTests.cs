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
    public void One_slab_leaving_a_census_is_routine()
    {
        // Charizard #146 psa 10 went 250 -> 249 on 2026-08-10. A slab was
        // cracked, crossed over, or regraded. The card left the cell; the
        // grader did not change how it counts.
        var report = Report(Grades(grade: 10, count: 249));
        var lastKnown = new Dictionary<(string, short), int> { [("psa", 10)] = 250 };

        Assert.Empty(PopulationRestatement.Anomalies(report, lastKnown));
    }

    [Fact]
    public void A_couple_of_slabs_leaving_a_large_census_is_routine()
    {
        // cgc 10 went 32 -> 30 on 2026-08-05: attrition, not a recount.
        var report = Report(new int[10], Grades(grade: 10, count: 30));
        var lastKnown = new Dictionary<(string, short), int> { [("cgc", 10)] = 32 };

        Assert.Empty(PopulationRestatement.Anomalies(report, lastKnown));
    }

    [Fact]
    public void A_small_cell_losing_one_of_its_few_is_routine()
    {
        // Dark Pupitar #41 psa 8 went 6 -> 5. A sixth of the cell, but still
        // one slab: percentages are meaningless this small.
        var report = Report(Grades(grade: 8, count: 5));
        var lastKnown = new Dictionary<(string, short), int> { [("psa", 8)] = 6 };

        Assert.Empty(PopulationRestatement.Anomalies(report, lastKnown));
    }

    [Fact]
    public void A_cell_losing_a_fifth_of_itself_is_a_restatement()
    {
        // Card 959116 psa 7 went 10 -> 7 on 2026-08-05, alongside three of its
        // sibling grades dropping the same day. That is PSA recounting a card.
        var report = Report(Grades(grade: 7, count: 7));
        var lastKnown = new Dictionary<(string, short), int> { [("psa", 7)] = 10 };

        var anomaly = Assert.Single(PopulationRestatement.Anomalies(report, lastKnown));
        Assert.Equal(PopulationAnomalyKind.Decrease, anomaly.Kind);
        Assert.Equal(10, anomaly.Previous);
        Assert.Equal(7, anomaly.Current);
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
