namespace PokemonInvestBatch.Domain.Parsing;

/// <summary>
/// Graded population census as shown on a card page. Index <c>i</c> holds the
/// population at grade <c>i + 1</c> (the site's chart axis is grades 1..10).
/// </summary>
public sealed record PopulationReport
{
    public required IReadOnlyList<int> Psa { get; init; }

    public required IReadOnlyList<int> Cgc { get; init; }

    // Sequence equality, not the record default (which would compare the two
    // list REFERENCES and call every re-parse unequal). One live consumer:
    // Parse_is_deterministic_across_identical_fetches asserts two parses of
    // the same page are equal — precisely the semantics reference equality
    // breaks. Verified the hard way on 2026-08-12: an audit called these
    // overrides dead, removing them failed exactly that test, and they went
    // straight back.
    public bool Equals(PopulationReport? other) =>
        other is not null && Psa.SequenceEqual(other.Psa) && Cgc.SequenceEqual(other.Cgc);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var n in Psa)
        {
            hash.Add(n);
        }

        foreach (var n in Cgc)
        {
            hash.Add(n);
        }

        return hash.ToHashCode();
    }
}
