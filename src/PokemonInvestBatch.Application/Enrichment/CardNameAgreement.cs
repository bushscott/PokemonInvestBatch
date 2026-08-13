namespace PokemonInvestBatch.Application.Enrichment;

/// <summary>
/// The confirmation gate: a number match is only trusted when the names
/// agree. Names agree when their folded forms are equal after the known
/// synonym classes are unified — the two catalogs genuinely name the same
/// physical card differently in exactly these ways (measured at ~3% of an
/// executed 283-card join, all in these classes):
///
///   Electric Energy / Lightning Energy · Dark Energy / Darkness Energy ·
///   Steel Energy / Metal Energy · Nidoran / Nidoran♂ · Pokemon / Pokémon ·
///   VStar / VSTAR
///
/// The substitutions apply to both sides, so over-application ("Dark
/// Charizard" becoming "DARKNESS CHARIZARD" on each) can never break an
/// equality — it can only create one, which is the point.
///
/// Deliberately no distance metric: fuzzy agreement is how a wrong number
/// gets silently accepted, and the whole reason this gate exists is to
/// refuse exactly that.
/// </summary>
public static class CardNameAgreement
{
    public static bool Agree(string priceChartingBaseName, string tcgdexName) =>
        Comparable(priceChartingBaseName) == Comparable(tcgdexName);

    /// <summary>The folded, synonym-unified form both names are reduced to.</summary>
    public static string Comparable(string name)
    {
        var tokens = NameFold.Fold(name).Split(' ');
        for (var i = 0; i < tokens.Length; i++)
        {
            tokens[i] = tokens[i] switch
            {
                "ELECTRIC" => "LIGHTNING",
                "DARK" => "DARKNESS",
                "STEEL" => "METAL",
                _ => tokens[i],
            };
        }

        return string.Join(' ', tokens);
    }
}
