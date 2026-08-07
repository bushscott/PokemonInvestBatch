namespace PokemonInvestBatch.Application.Scheduling;

/// <summary>One scoring candidate: the card's id plus the three facts the
/// scorer reads. Nothing else leaves the database until a winner is picked.</summary>
public sealed record VisitCandidate
{
    public required long Id { get; init; }

    public required CardVisitState State { get; init; }
}

public enum VisitChoiceKind
{
    /// <summary>Retry a benched card early, ahead of its comeback date.</summary>
    RetryBenched,

    /// <summary>Visit the highest-scoring candidate the pool offered.</summary>
    Scored,

    /// <summary>Every scored candidate ranks below a never-visited card. Take an
    /// unvisited card instead — falling back to <see cref="VisitChoice.CardId"/>,
    /// the scored runner-up, if it turns out none are left.</summary>
    PreferUnvisited,
}

/// <summary>What the detail lane should do next. The lane owns the queries;
/// this owns the ranking.</summary>
public sealed record VisitChoice
{
    public required VisitChoiceKind Kind { get; init; }

    /// <summary>The card to visit, except under <see cref="VisitChoiceKind.PreferUnvisited"/>
    /// where it is the runner-up to use if no unvisited card exists — null when
    /// the pool was empty and there is nothing to fall back to.</summary>
    public long? CardId { get; init; }

    public static VisitChoice RetryBenched(long cardId) =>
        new() { Kind = VisitChoiceKind.RetryBenched, CardId = cardId };

    public static VisitChoice Scored(long cardId) =>
        new() { Kind = VisitChoiceKind.Scored, CardId = cardId };

    public static VisitChoice PreferUnvisited(long? fallbackCardId) =>
        new() { Kind = VisitChoiceKind.PreferUnvisited, CardId = fallbackCardId };
}

/// <summary>
/// Which card the detail lane visits next, as a pure ranking over what the
/// database already handed back.
///
/// This exists as its own class because the bug it now guards was expensive:
/// the lane once short-circuited to "unvisited first" *before* scoring, which
/// silently disabled the burn-window tier for as long as any unvisited backlog
/// existed — 46,997 cards at the time — so hot cards quietly lost sales that
/// the whole scheduler was built to protect. It was caught by a human watching
/// a dashboard tile, because no test could reach the decision while it lived
/// inside a method that needed a database to run.
/// </summary>
public static class VisitSelection
{
    public static VisitChoice Choose(
        long? benchRetryId,
        IReadOnlyList<VisitCandidate> candidates,
        DateTimeOffset now,
        VisitPriorityOptions options)
    {
        // A benched card only reaches the lane through the recheck's own slot,
        // which has already decided this is the moment. Nothing outranks it.
        if (benchRetryId is { } retry)
        {
            return VisitChoice.RetryBenched(retry);
        }

        var winner = candidates.Count == 0
            ? null
            : candidates.MaxBy(c => VisitPriority.Score(c.State, now, options));
        var winnerScore = winner is null
            ? double.NegativeInfinity
            : VisitPriority.Score(winner.State, now, options);

        // Never-visited cards carry a null last-visit, so they sort out of every
        // staleness-ordered window the pool queries and can never appear among
        // the candidates at all. Their tier is applied here by comparison
        // instead — and it is a comparison, not a short-circuit, precisely so a
        // burn-window-due card can still outrank the entire unvisited backlog.
        var unvisitedScore = VisitPriority.Score(new CardVisitState(), now, options);
        return winnerScore < unvisitedScore
            ? VisitChoice.PreferUnvisited(winner?.Id)
            : VisitChoice.Scored(winner!.Id);
    }
}
