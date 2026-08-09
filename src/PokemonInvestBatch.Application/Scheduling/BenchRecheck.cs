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
/// closes the slot — and every consecutive failure doubles the stand-down,
/// because a bench that keeps flunking its retries is a bench of genuinely
/// broken cards, and re-confirming that costs a crawl slot whose price
/// inverts with AIMD: noise at the ten-second floor, half of everything at
/// the five-minute ceiling. One success resets the backoff, so a healed
/// site is still noticed within the base interval. A queue that visibly
/// refuses to drain is itself the alarm — a card in it keeps failing and
/// deserves a look. Pure state — no clock, no I/O; the detail lane owns the
/// only instance and reports each retry's outcome back.
/// </summary>
public sealed class BenchRecheck(TimeSpan interval)
{
    /// <summary>With the default ten-minute interval the longest stand-down is
    /// 2^5 × 10m ≈ five hours — rare enough to be cheap at the AIMD ceiling,
    /// frequent enough that a healed site is noticed the same afternoon.</summary>
    private const int MaxDoublings = 5;

    private DateTimeOffset? _lastFailureAt;

    private int _consecutiveFailures;

    /// <summary>Open unless a failed retry closed it within the current
    /// stand-down: the base interval after one failure, doubling per
    /// consecutive failure up to the cap.</summary>
    public bool IsSlotOpen(DateTimeOffset now) =>
        _lastFailureAt is not { } last
        || now - last >= interval * Math.Pow(2, Math.Min(_consecutiveFailures - 1, MaxDoublings));

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

    public void RecordSuccess()
    {
        _lastFailureAt = null;
        _consecutiveFailures = 0;
    }

    public void RecordFailure(DateTimeOffset now)
    {
        _lastFailureAt = now;
        _consecutiveFailures++;
    }
}
