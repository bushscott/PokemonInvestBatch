using PokemonInvestBatch.Domain.Parsing;
using PokemonInvestBatch.Domain.Tests.Fixtures;

namespace PokemonInvestBatch.Domain.Tests.Parsing;

public class PageFingerprintTests
{
    [Fact]
    public void Identical_pages_have_identical_fingerprints()
    {
        var a = PageFingerprint.OfCardDetailPage(Fixture.Load("charizard-live-a"));
        var b = PageFingerprint.OfCardDetailPage(Fixture.Load("charizard-live-b"));

        Assert.Equal(a.Hash, b.Hash);
    }

    [Fact]
    public void Pop_schema_generations_produce_different_fingerprints()
    {
        // The 2024 {"pop":[...]} page and the current {"psa","cgc"} page must
        // fingerprint differently — this is the tripwire that would have
        // caught the census schema change the day it shipped.
        var old = PageFingerprint.OfCardDetailPage(Fixture.Load("charizard-2024-06-pop-schema"));
        var current = PageFingerprint.OfCardDetailPage(Fixture.Load("charizard-live-a"));

        Assert.NotEqual(old.Hash, current.Hash);
    }

    [Fact]
    public void Fingerprint_captures_structure_not_content()
    {
        // Same schema generation, different prices/sales/dates → same fingerprint.
        var june = PageFingerprint.OfCardDetailPage(Fixture.Load("charizard-2026-06-psa-cgc"));
        var live = PageFingerprint.OfCardDetailPage(Fixture.Load("charizard-live-a"));

        Assert.Equal(june.Hash, live.Hash);
    }

    [Fact]
    public void Names_json_lists_the_structures_it_saw()
    {
        var print = PageFingerprint.OfCardDetailPage(Fixture.Load("charizard-live-a"));

        Assert.Contains("chart_data", print.Names);
        Assert.Contains("psa", print.Names);
        Assert.Contains("completed-auctions-manual-only", print.Names);
    }

    [Fact]
    public void Hash_is_a_64_char_hex_sha256()
    {
        var print = PageFingerprint.OfCardDetailPage(Fixture.Load("charizard-live-a"));

        Assert.Matches("^[0-9a-f]{64}$", print.Hash);
    }
}
