using System.Text.Json;

namespace PokemonInvestBatch.Domain.Parsing;

/// <summary>
/// Every name a fingerprint uses, qualified by the bucket it appeared in
/// ("chart_data:psa10").
///
/// The hash asks whether we have seen this exact combination before, and on
/// this site the answer is routinely no for innocent reasons: a fingerprint counts
/// how much data a card carries, not only how the page is built. A promo with
/// one price tier and no census is a combination never seen and a card in
/// perfect health. The vocabulary asks the question the alarm is actually
/// for — is there a name here we have never seen anywhere? An unaccounted
/// name is the site's markup moving. A smaller helping of familiar names is
/// just a quiet card.
/// </summary>
public static class FingerprintVocabulary
{
    /// <summary>Every "bucket:name" pair the fingerprint carries, in order.</summary>
    public static IReadOnlyCollection<string> Of(string namesJson)
    {
        using var document = JsonDocument.Parse(namesJson);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var bucket in document.RootElement.EnumerateObject())
        {
            foreach (var name in bucket.Value.EnumerateArray())
            {
                names.Add($"{bucket.Name}:{name.GetString()}");
            }
        }

        return names;
    }

    /// <summary>
    /// The names this fingerprint uses that appear in none of those already
    /// known. Empty means a new arrangement of familiar parts — worth
    /// archiving, but there is nothing to tell anyone.
    /// </summary>
    public static IReadOnlyCollection<string> NamesAbsentFrom(
        string namesJson, IEnumerable<string> knownFingerprints)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fingerprint in knownFingerprints)
        {
            known.UnionWith(Of(fingerprint));
        }

        return [.. Of(namesJson).Where(name => !known.Contains(name))];
    }
}
