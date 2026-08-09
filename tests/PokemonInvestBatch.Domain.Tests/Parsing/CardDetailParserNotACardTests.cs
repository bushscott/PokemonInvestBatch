using PokemonInvestBatch.Domain.Parsing;
using PokemonInvestBatch.Domain.Tests.Fixtures;

namespace PokemonInvestBatch.Domain.Tests.Parsing;

/// <summary>
/// The pokemon-mini fixture is a real capture of a page this parser silently
/// accepted for a day, writing 421 months of handheld-console prices into the
/// card corpus under grade-tier names. It parses perfectly: same markup, same
/// chart series, USD, image, everything. Only the condition labels ever said
/// otherwise.
/// </summary>
public class CardDetailParserNotACardTests
{
    [Fact]
    public void A_console_page_is_refused()
    {
        var e = Assert.Throws<NotACardPageException>(
            () => CardDetailParser.Parse(Fixture.Load("pokemon-mini-pinball")));

        Assert.Contains("no card grade", e.Message);
    }

    // The distinction is the entire point: drift means the site changed and
    // feeds the parse-failure alarm; a console page means the catalog handed us
    // the wrong product and must never read as a site-wide outage.
    [Fact]
    public void Refusing_a_console_page_is_not_schema_drift()
    {
        Assert.IsNotType<SchemaDriftException>(
            Record.Exception(() => CardDetailParser.Parse(Fixture.Load("pokemon-mini-pinball"))));
    }

    [Theory]
    [InlineData("charizard-live-a")]
    [InlineData("charizard-live-b")]
    [InlineData("charizard-2026-06-psa-cgc")]
    public void Real_card_pages_are_still_accepted(string fixture)
    {
        var page = CardDetailParser.Parse(Fixture.Load(fixture));

        Assert.NotEmpty(page.Chart);
    }

    // The Wayback fixtures are drift specimens — they are meant to throw. What
    // matters is that the new card check does not intercept them first and
    // relabel a genuine schema change as "not a card", which would silently
    // retire the alarm that exists to catch the site moving under us.
    [Theory]
    [InlineData("charizard-2024-06-pop-schema")]
    [InlineData("charizard-2025-01-pop-schema")]
    public void Older_schema_generations_still_read_as_drift(string fixture)
    {
        Assert.Throws<SchemaDriftException>(
            () => CardDetailParser.Parse(Fixture.Load(fixture)));
    }

    [Fact]
    public void One_known_grade_is_enough_to_prove_a_card()
    {
        // The day pricecharting adds an eleventh grading company, every card
        // page gains a label this vocabulary has never seen. Demanding that all
        // labels be known would bench the entire corpus overnight.
        Assert.True(GradeTierVocabulary.LooksLikeCard(
            ["Ungraded", "Grade 7", "WHATNOT 10"]));
    }

    [Fact]
    public void Console_conditions_contain_no_card_grade()
    {
        Assert.False(GradeTierVocabulary.LooksLikeCard(
            ["Loose", "CIB", "New", "Graded New", "Graded CIB", "Box Only", "Manual Only"]));
    }

    [Fact]
    public void Labels_mangled_by_the_sites_unclosed_spans_still_match()
    {
        // TextContent on "Box<span> Only<span>\n (0)" arrives with newlines and
        // runs of spaces baked in; comparing raw would miss on whitespace alone.
        Assert.True(GradeTierVocabulary.LooksLikeCard(["Ungraded\n   "]));
        Assert.Equal("Box Only", GradeTierVocabulary.Normalize("Box\n  Only\n "));
    }

    [Fact]
    public void Console_hardware_is_refused_too()
    {
        // A second real capture, a handheld console rather than a game — its
        // condition selector still speaks the game vocabulary.
        var e = Assert.Throws<NotACardPageException>(
            () => CardDetailParser.Parse(Fixture.Load("pokemon-mini-pikachu-color")));

        Assert.Contains("no card grade", e.Message);
    }

    [Fact]
    public void A_game_page_with_no_sales_is_convicted_by_its_genre()
    {
        // The condition selector only renders once something has sold, so a
        // game nobody has bought would slip a selector-only check and write
        // its chart into the corpus in silence. The Genre row — "Systems" on
        // this capture, "Pokemon Card" on a real card — is the witness that
        // does not need a sale to exist.
        var html = WithoutConditionSelector(Fixture.Load("pokemon-mini-pikachu-color"));

        var e = Assert.Throws<NotACardPageException>(() => CardDetailParser.Parse(html));

        Assert.Contains("Genre", e.Message);
    }

    [Fact]
    public void Unknown_tiers_under_a_card_genre_read_as_drift_not_retirement()
    {
        // If every tier label is unrecognized but the page says it is a card,
        // the site changed its words. That is an emergency for the drift
        // alarm — never a quiet, permanent retirement of real cards one
        // visit at a time.
        var html = Fixture.Load("pokemon-mini-pinball").Replace("Arcade", "Pokemon Card");

        Assert.Throws<SchemaDriftException>(() => CardDetailParser.Parse(html));
    }

    private static string WithoutConditionSelector(string html)
    {
        var start = html.IndexOf("<select id=\"completed-auctions-condition\"", StringComparison.Ordinal);
        var end = html.IndexOf("</select>", start, StringComparison.Ordinal) + "</select>".Length;
        return html[..start] + html[end..];
    }
}
