namespace PokemonInvestBatch.Infrastructure.Persistence;

/// <summary>
/// Reading a change-only history backwards. Price and population rows are
/// appended only when a value differs from the last observation, so "what do
/// we currently believe about this cell?" is the newest row for its key — not
/// a column anyone can select. Getting this wrong reads a stale value as
/// current and writes a duplicate row that says nothing changed.
/// </summary>
public static class LastObserved
{
    public static Dictionary<TKey, TValue> ByKey<TRow, TKey, TValue>(
        IEnumerable<TRow> rows,
        Func<TRow, TKey> key,
        Func<TRow, DateTimeOffset> observedAt,
        Func<TRow, TValue> value)
        where TKey : notnull =>
        rows.GroupBy(key).ToDictionary(g => g.Key, g => value(g.MaxBy(observedAt)!));
}
