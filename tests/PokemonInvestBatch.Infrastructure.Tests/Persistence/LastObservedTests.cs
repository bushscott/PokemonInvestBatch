using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Infrastructure.Tests.Persistence;

/// <summary>
/// Reading a change-only history backwards. Getting this wrong is quiet rather
/// than loud: a stale value read as current makes the change planner write a
/// duplicate row asserting that nothing changed, which corrupts the history it
/// is supposed to protect without ever throwing.
/// </summary>
public class LastObservedTests
{
    private sealed record Row(string Key, DateTimeOffset ObservedAt, int Value);

    private static readonly DateTimeOffset Day1 = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private static Dictionary<string, int> Reduce(params Row[] rows) =>
        LastObserved.ByKey(rows, r => r.Key, r => r.ObservedAt, r => r.Value);

    [Fact]
    public void The_newest_observation_of_a_key_wins()
    {
        var result = Reduce(
            new Row("psa-9", Day1, 100),
            new Row("psa-9", Day1.AddDays(5), 140));

        Assert.Equal(140, result["psa-9"]);
    }

    [Fact]
    public void Row_order_does_not_matter()
    {
        // Nothing guarantees the database hands rows back in date order, and
        // the query does not ask it to.
        var result = Reduce(
            new Row("psa-9", Day1.AddDays(5), 140),
            new Row("psa-9", Day1, 100),
            new Row("psa-9", Day1.AddDays(2), 120));

        Assert.Equal(140, result["psa-9"]);
    }

    [Fact]
    public void Keys_are_reduced_independently()
    {
        var result = Reduce(
            new Row("psa-9", Day1.AddDays(5), 140),
            new Row("psa-10", Day1, 900),
            new Row("psa-9", Day1, 100));

        Assert.Equal(140, result["psa-9"]);
        Assert.Equal(900, result["psa-10"]);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void A_card_with_no_history_reduces_to_nothing()
    {
        // The first visit to a card: every value it reports is new.
        Assert.Empty(Reduce());
    }

    [Fact]
    public void A_value_that_went_back_down_is_still_the_newest_one()
    {
        // Populations are not supposed to shrink, but when the source restates
        // them they do. The newest number is what the source now says, and the
        // restatement alarm is what decides whether to believe it.
        var result = Reduce(
            new Row("psa-8", Day1, 8),
            new Row("psa-8", Day1.AddDays(3), 6));

        Assert.Equal(6, result["psa-8"]);
    }
}
