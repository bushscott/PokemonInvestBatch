using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using PokemonInvestBatch.Infrastructure.Persistence;

namespace PokemonInvestBatch.Infrastructure.Tests.Persistence;

/// <summary>
/// The schema guarantees agreed in design, asserted against the compiled EF
/// model — no database needed. These encode never-overwrite and dedup rules
/// at the metadata level so a careless mapping change fails the build.
/// </summary>
public class SchemaModelTests
{
    private static IModel Model()
    {
        var options = new DbContextOptionsBuilder<PokemonDbContext>()
            .UseNpgsql("Host=model-only")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var context = new PokemonDbContext(options);
        return context.Model;
    }

    [Fact]
    public void Sales_are_deduped_by_marketplace_natural_key()
    {
        var sale = Model().FindEntityType(typeof(Sale))!;

        var index = sale.GetIndexes().SingleOrDefault(i =>
            i.IsUnique &&
            i.Properties.Select(p => p.Name).SequenceEqual([nameof(Sale.Source), nameof(Sale.SourceId)]));

        Assert.NotNull(index);
    }

    [Fact]
    public void Price_history_is_change_only_append()
    {
        var entity = Model().FindEntityType(typeof(CardPriceMonth))!;

        Assert.Equal(
            [
                nameof(CardPriceMonth.CardId),
                nameof(CardPriceMonth.Tier),
                nameof(CardPriceMonth.Month),
                nameof(CardPriceMonth.ObservedAt),
            ],
            entity.FindPrimaryKey()!.Properties.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Population_history_is_change_only_append()
    {
        var entity = Model().FindEntityType(typeof(CardPopulation))!;

        Assert.Equal(
            [
                nameof(CardPopulation.CardId),
                nameof(CardPopulation.Grader),
                nameof(CardPopulation.Grade),
                nameof(CardPopulation.ObservedAt),
            ],
            entity.FindPrimaryKey()!.Properties.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Cards_use_pricecharting_product_ids_verbatim()
    {
        var id = Model().FindEntityType(typeof(Card))!.FindProperty(nameof(Card.Id))!;

        Assert.Equal(ValueGenerated.Never, id.ValueGenerated);
    }

    [Fact]
    public void Sets_are_unique_by_slug()
    {
        var set = Model().FindEntityType(typeof(CardSet))!;

        var index = set.GetIndexes().SingleOrDefault(i =>
            i.IsUnique &&
            i.Properties.Select(p => p.Name).SequenceEqual([nameof(CardSet.Slug)]));

        Assert.NotNull(index);
    }

    [Fact]
    public void Refresh_requests_are_served_from_a_partial_index()
    {
        // Pending asks are rare, so the pick scan pays for a sliver of index,
        // not a column-wide one over ~90k cards.
        var card = Model().FindEntityType(typeof(Card))!;

        var index = card.GetIndexes().SingleOrDefault(i =>
            i.Properties.Select(p => p.Name).SequenceEqual([nameof(Card.RefreshRequestedAt)]));

        Assert.NotNull(index);
        Assert.Equal("refresh_requested_at IS NOT NULL", index.GetFilter());
    }

    [Fact]
    public void Page_fingerprints_are_keyed_by_hash()
    {
        var fingerprint = Model().FindEntityType(typeof(KnownFingerprint))!;

        Assert.Equal(
            [nameof(KnownFingerprint.Hash)],
            fingerprint.FindPrimaryKey()!.Properties.Select(p => p.Name).ToArray());
    }

    [Fact]
    public void Tcgdex_enrichment_is_change_only_append()
    {
        // ADR-0009: verdicts are appended when they differ, never updated in
        // place — the PK ending in ComputedAt makes that structural, exactly
        // like the two observational histories.
        var entity = Model().FindEntityType(typeof(TcgdexEnrichment))!;

        Assert.Equal(
            [nameof(TcgdexEnrichment.CardId), nameof(TcgdexEnrichment.ComputedAt)],
            entity.FindPrimaryKey()!.Properties.Select(p => p.Name).ToArray());
    }
}
