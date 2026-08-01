namespace PokemonInvestBatch.Application.Scheduling;

/// <summary>Scheduler view of a benched card — just enough to pick the next
/// one to retry.</summary>
public sealed record BenchedCandidate
{
    public required long Id { get; init; }

    public required DateTimeOffset QuarantinedUntil { get; init; }
}

/// <summary>
/// Plain meaning: cards in the retry queue get retried early, one at a time. A retry
/// that succeeds keeps the slot open — whatever was broken may be fixed for
/// every benched card, so the retry queue drains back-to-back and the
/// dashboard shows it emptying within minutes of a fix. A retry that fails
/// closes the slot for the interval: still broken, stand down. A queue that
/// visibly refuses to drain is itself the alarm — a card in it keeps
/// failing and deserves a look. Pure state — no clock, no I/O; the detail
/// lane owns the only instance and reports each retry's outcome back.
/// </summary>
public sealed class BenchRecheck(TimeSpan interval)
{
    private DateTimeOffset? _lastFailureAt;

    /// <summary>Open unless a failed retry closed it within the interval.</summary>
    public bool IsSlotOpen(DateTimeOffset now) =>
        _lastFailureAt is not { } last || now - last >= interval;

    /// <summary>The next benched card to retry — soonest comeback first, so a
    /// re-benched failure's doubled sentence sends it to the back — or null
    /// if the slot is closed or the bench is empty.</summary>
    public long? TrySelect(IReadOnlyList<BenchedCandidate> benched, DateTimeOffset now)
    {
        if (!IsSlotOpen(now))
        {
            return null;
        }

        return benched.OrderBy(b => b.QuarantinedUntil).FirstOrDefault()?.Id;
    }

    public void RecordSuccess() => _lastFailureAt = null;

    public void RecordFailure(DateTimeOffset now) => _lastFailureAt = now;
}
