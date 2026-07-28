using PokemonInvestBatch.Domain.Parsing;

namespace PokemonInvestBatch.Infrastructure.Persistence;

/// <summary>
/// Decides which rows a parsed page adds to history: a row is appended only
/// when its value differs from the last observation, with "never observed"
/// defaulting to zero. Nothing is ever overwritten; unchanged facts are
/// never re-written. Pure — the caller supplies last-known values.
/// </summary>
public static class ChangeOnlyPlanner
{
    public static List<CardPriceMonth> NewPricePoints(
        long cardId,
        IReadOnlyDictionary<PriceTier, IReadOnlyList<PricePoint>> chart,
        IReadOnlyDictionary<(PriceTier Tier, DateOnly Month), int> lastKnown,
        DateTimeOffset observedAt) =>
        [
            .. chart.SelectMany(series => series.Value
                .Where(point => point.PriceCents != lastKnown.GetValueOrDefault((series.Key, point.Month)))
                .Select(point => new CardPriceMonth
                {
                    CardId = cardId,
                    Tier = series.Key,
                    Month = point.Month,
                    PriceCents = point.PriceCents,
                    ObservedAt = observedAt,
                })),
        ];

    public static List<CardPopulation> NewPopulationCells(
        long cardId,
        PopulationReport population,
        IReadOnlyDictionary<(string Grader, short Grade), int> lastKnown,
        DateTimeOffset observedAt) =>
        [
            .. new[] { ("psa", population.Psa), ("cgc", population.Cgc) }
                .SelectMany(grader => grader.Item2.Select((count, index) => new CardPopulation
                {
                    CardId = cardId,
                    Grader = grader.Item1,
                    Grade = (short)(index + 1),
                    Population = count,
                    ObservedAt = observedAt,
                }))
                .Where(cell => cell.Population != lastKnown.GetValueOrDefault((cell.Grader, cell.Grade))),
        ];
}
