using PokemonInvestBatch.Domain.Parsing;
using PokemonInvestBatch.Domain.Tests.Fixtures;

namespace PokemonInvestBatch.Domain.Tests.Parsing;

/// <summary>
/// Guards the charizard-burst fixture: charizard-live-a with its graded bucket
/// rewritten as a 30-row five-day burst (the set-contagion tests' trigger). If
/// the fixture ever drifts out of parseability, this fails locally instead of
/// only in the DB-backed suite on CI.
/// </summary>
public class BurstFixtureTests
{
    [Fact]
    public void The_burst_fixture_carries_a_full_five_day_bucket()
    {
        var page = CardDetailParser.Parse(Fixture.Load("charizard-burst"));

        // 30 = SalesObservation.BucketCap; Domain.Tests deliberately does not
        // reference Application, so the number is spelled out here.
        var burstBucket = page.Sales
            .GroupBy(s => s.GradeTier)
            .Single(bucket => bucket.All(s => s.SourceId.StartsWith("burst", StringComparison.Ordinal)));

        Assert.Equal(30, burstBucket.Count());
        Assert.Equal(new DateOnly(2026, 6, 20), burstBucket.Min(s => s.SoldOn));
        Assert.Equal(new DateOnly(2026, 6, 24), burstBucket.Max(s => s.SoldOn));
        Assert.Equal(30, burstBucket.Select(s => s.SourceId).Distinct().Count());
    }
}
