using Microsoft.EntityFrameworkCore;
using PokemonInvestBatch.Domain.Parsing;

namespace PokemonInvestBatch.Infrastructure.Persistence;

public class PokemonDbContext(DbContextOptions<PokemonDbContext> options) : DbContext(options)
{
    public DbSet<CardSet> Sets => Set<CardSet>();

    public DbSet<Card> Cards => Set<Card>();

    public DbSet<CardPriceMonth> PriceMonths => Set<CardPriceMonth>();

    public DbSet<CardPopulation> Populations => Set<CardPopulation>();

    public DbSet<Sale> Sales => Set<Sale>();

    public DbSet<PageVisit> Visits => Set<PageVisit>();

    public DbSet<KnownFingerprint> Fingerprints => Set<KnownFingerprint>();

    public DbSet<ParseFailure> ParseFailures => Set<ParseFailure>();

    public DbSet<TcgdexEnrichment> TcgdexEnrichments => Set<TcgdexEnrichment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CardSet>(set =>
        {
            set.HasIndex(s => s.Slug).IsUnique();
            set.Property(s => s.Slug).HasMaxLength(200);
            set.Property(s => s.Name).HasMaxLength(200);
        });

        modelBuilder.Entity<Card>(card =>
        {
            // PriceCharting's product id, never generated locally.
            card.Property(c => c.Id).ValueGeneratedNever();
            card.Property(c => c.Url).HasMaxLength(ProductListing.MaxUrlLength);
            card.Property(c => c.Name).HasMaxLength(300);
            // Was 64 (plain sha256 hex); the site now serves "ref"-prefixed
            // 67-char tokens. 128 leaves room for the next format surprise.
            card.Property(c => c.ImageHash).HasMaxLength(128);
            card.HasOne(c => c.Set).WithMany().HasForeignKey(c => c.SetId).OnDelete(DeleteBehavior.Restrict);
            // The scheduler's oldest-first / priority scans.
            card.HasIndex(c => c.LastVisitedAt);
            // The intake tier's pick scan; partial because pending asks are rare.
            card.HasIndex(c => c.RefreshRequestedAt).HasFilter("refresh_requested_at IS NOT NULL");
            // The probe's due scan over machine-retired cards; partial for the
            // same reason — gone cards are rare.
            card.HasIndex(c => c.GoneAt).HasFilter("gone_at IS NOT NULL");
        });

        modelBuilder.Entity<CardPriceMonth>(price =>
        {
            // Change-only append: one row per observation that differed.
            price.HasKey(p => new { p.CardId, p.Tier, p.Month, p.ObservedAt });
            price.HasOne<Card>().WithMany().HasForeignKey(p => p.CardId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CardPopulation>(population =>
        {
            population.HasKey(p => new { p.CardId, p.Grader, p.Grade, p.ObservedAt });
            population.Property(p => p.Grader).HasMaxLength(8);
            population.HasOne<Card>().WithMany().HasForeignKey(p => p.CardId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Sale>(sale =>
        {
            // The dedup guarantee; the unnest insert's ON CONFLICT target.
            sale.HasIndex(s => new { s.Source, s.SourceId }).IsUnique();
            sale.Property(s => s.Source).HasMaxLength(16);
            sale.Property(s => s.SourceId).HasMaxLength(SaleRecord.MaxSourceIdLength);
            sale.Property(s => s.GradeTier).HasMaxLength(SaleRecord.MaxGradeTierLength);
            sale.Property(s => s.Title).HasMaxLength(SaleRecord.MaxTitleLength);
            sale.HasOne<Card>().WithMany().HasForeignKey(s => s.CardId).OnDelete(DeleteBehavior.Restrict);
            sale.HasIndex(s => new { s.CardId, s.SoldOn });
        });

        modelBuilder.Entity<PageVisit>(visit =>
        {
            visit.Property(v => v.Url).HasMaxLength(500);
            visit.Property(v => v.FingerprintHash).HasMaxLength(64);
            // Rolling failure-rate window reads recent visits.
            visit.HasIndex(v => v.FetchedAt);
        });

        modelBuilder.Entity<KnownFingerprint>(fingerprint =>
        {
            fingerprint.HasKey(f => f.Hash);
            fingerprint.Property(f => f.Hash).HasMaxLength(64);
            fingerprint.Property(f => f.SampleUrl).HasMaxLength(500);
            fingerprint.Property(f => f.Names).HasColumnType("jsonb");
        });

        modelBuilder.Entity<ParseFailure>(failure =>
        {
            failure.Property(f => f.Url).HasMaxLength(500);
            failure.Property(f => f.FingerprintHash).HasMaxLength(64);
            failure.HasIndex(f => f.FetchedAt);
        });

        modelBuilder.Entity<TcgdexEnrichment>(enrichment =>
        {
            // Change-only append (ADR-0009): one row per verdict that
            // differed; latest per card is the current verdict.
            enrichment.HasKey(e => new { e.CardId, e.ComputedAt });
            enrichment.HasOne<Card>().WithMany().HasForeignKey(e => e.CardId).OnDelete(DeleteBehavior.Restrict);
            enrichment.Property(e => e.CardNumber).HasMaxLength(32);
            enrichment.Property(e => e.TcgdexSetId).HasMaxLength(32);
            enrichment.Property(e => e.TcgdexCardId).HasMaxLength(64);
            enrichment.Property(e => e.TcgdexName).HasMaxLength(300);
            enrichment.Property(e => e.TcgdexVersion).HasMaxLength(64);
        });
    }
}
