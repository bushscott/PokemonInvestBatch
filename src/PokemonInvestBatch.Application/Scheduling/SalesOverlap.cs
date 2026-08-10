namespace PokemonInvestBatch.Application.Scheduling;

/// <summary>
/// How much of a visit's page we had already seen, per grade bucket.
///
/// This is the only evidence that answers "did rows scroll off unseen?", and it
/// answers it without knowing how many rows a bucket holds — which matters,
/// because that number is not a constant. Every graded bucket renders exactly
/// 30 rows, but the Ungraded one renders 50 or 60 depending on the page
/// (measured across the archived page corpus 2026-08-10), and the page's own
/// condition selector is no help: it reports what it rendered, never a larger
/// total. Any constant would either cry wolf on the big buckets or stay silent
/// on real losses.
/// </summary>
/// <param name="RowsHeldBefore">Rows already stored for this card in each
/// bucket, before the visit wrote anything.</param>
/// <param name="RowsNewlyWritten">Rows from this page the database had never
/// seen. Matching the page's own count means the two share nothing.</param>
public sealed record SalesOverlap(
    IReadOnlyDictionary<string, int> RowsHeldBefore,
    IReadOnlyDictionary<string, int> RowsNewlyWritten)
{
    public int HeldBefore(string gradeTier) => RowsHeldBefore.GetValueOrDefault(gradeTier);

    public int NewlyWritten(string gradeTier) => RowsNewlyWritten.GetValueOrDefault(gradeTier);

    /// <summary>
    /// Reads the overlap off the card's sale counts either side of the write.
    /// Counting rows before and after is deliberately blunter than asking the
    /// insert which keys collided: it needs no RETURNING clause, so the one
    /// hand-written SQL statement in the codebase stays a plain append.
    /// </summary>
    public static SalesOverlap Between(
        IReadOnlyDictionary<string, int> heldBefore,
        IReadOnlyDictionary<string, int> heldAfter)
    {
        var written = new Dictionary<string, int>();
        foreach (var (tier, after) in heldAfter)
        {
            var delta = after - heldBefore.GetValueOrDefault(tier);
            if (delta > 0)
            {
                written[tier] = delta;
            }
        }

        return new SalesOverlap(heldBefore, written);
    }
}
