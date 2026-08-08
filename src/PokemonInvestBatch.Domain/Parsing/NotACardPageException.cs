namespace PokemonInvestBatch.Domain.Parsing;

/// <summary>
/// Plain meaning: this page is a real page and we parsed it fine — it just
/// isn't a card. A handheld console, a video game, an accessory.
///
/// Deliberately NOT a kind of <see cref="SchemaDriftException"/>. Drift means
/// "the site changed and the parser is now wrong", which is an emergency that
/// feeds the parse-failure-rate alarm and should wake someone. A console page
/// means "the catalog handed us the wrong product", which is a cataloging
/// mistake and must never be counted as drift — a set of them would otherwise
/// masquerade as a site-wide outage while the crawl is perfectly healthy.
///
/// The verdict is also permanent, which drift is not. A drifted page may parse
/// tomorrow; a Game Boy will never be a card.
/// </summary>
public sealed class NotACardPageException(string message) : Exception(message);
