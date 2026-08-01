namespace PokemonInvestBatch.Application.Scheduling;

/// <summary>Scheduler view of a benched card — just enough to decide whose
/// second chance is due.</summary>
public sealed record BenchedCandidate
{
    public required long Id { get; init; }

    public required int FailureStreak { get; init; }

    public required DateTimeOffset QuarantinedUntil { get; init; }
}

/// <summary>
/// Plain meaning: benched cards get retried early — one at a time, at most
/// one retry per interval — so the dashboard's retry queue drains itself
/// within hours of a fix instead of waiting out day-long sentences. The
/// interval is the safety valve: however many cards are benched, second
/// chances can never crowd out the healthy corpus. A retry that succeeds
/// clears the card's bench; one that fails re-benches it with a doubled
/// sentence, which also pushes its next second chance out. Pure state —
/// no clock, no I/O; the detail lane owns the only instance.
/// </summary>
public sealed class SecondChanceTrickle(TimeSpan interval)
{
    private DateTimeOffset? _lastRetryAt;

    /// <summary>Cheap pre-check so callers only query the bench when a retry
    /// slot is actually open.</summary>
    public bool IsSlotOpen(DateTimeOffset now) =>
        _lastRetryAt is not { } last || now - last >= interval;

    /// <summary>
    /// Picks the benched card whose second chance has been pending longest,
    /// or null if the slot is closed or nobody is due yet. Selecting a card
    /// consumes the slot; coming up empty does not.
    /// </summary>
    public long? TrySelect(IReadOnlyList<BenchedCandidate> benched, DateTimeOffset now)
    {
        if (!IsSlotOpen(now))
        {
            return null;
        }

        var due = benched
            .Where(b => QuarantinePolicy.SecondChanceAt(b.FailureStreak, b.QuarantinedUntil) <= now)
            .OrderBy(b => QuarantinePolicy.SecondChanceAt(b.FailureStreak, b.QuarantinedUntil))
            .FirstOrDefault();
        if (due is null)
        {
            return null;
        }

        _lastRetryAt = now;
        return due.Id;
    }
}
