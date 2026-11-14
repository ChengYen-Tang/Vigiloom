using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

namespace Vigiloom.Modules.SecurityAnalysis.Persistence;

internal static class IntelligenceModel
{
    internal static void Configure(ModelBuilder model)
    {
        var source = model.Entity<IntelligenceSource>();
        source.ToTable("IntelligenceSources", t =>
        {
            t.HasCheckConstraint("CK_IntelligenceSources_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'");
            t.HasCheckConstraint("CK_IntelligenceSources_Settings", "[UpdateIntervalSeconds] BETWEEN 300 AND 604800 AND [StaleAfterSeconds] BETWEEN 3600 AND 2592000 AND [MaximumBytes] BETWEEN 1024 AND 67108864 AND [MaximumRecords] BETWEEN 1 AND 1000000");
        });
        source.HasKey(x => x.Id).IsClustered(false);
        source.Property(x => x.Id).ValueGeneratedNever();
        source.Property(x => x.RowId).UseIdentityColumn();
        source.HasIndex(x => x.RowId).IsUnique().IsClustered();
        source.Property(x => x.Key).HasMaxLength(128).IsRequired();
        source.Property(x => x.UpdateIntervalSeconds).HasDefaultValue(86_400);
        source.Property(x => x.StaleAfterSeconds).HasDefaultValue(259_200);
        source.Property(x => x.MaximumBytes).HasDefaultValue(16 * 1024 * 1024);
        source.Property(x => x.MaximumRecords).HasDefaultValue(50_000);
        source.HasIndex(x => x.Key).IsUnique();
        source.Property(x => x.LastError).HasMaxLength(1024);
        source.Property(x => x.Version).IsRowVersion();

        var revision = model.Entity<IntelligenceRevision>();
        revision.ToTable("IntelligenceRevisions", t =>
        {
            t.HasCheckConstraint("CK_IntelligenceRevisions_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'");
            t.HasCheckConstraint("CK_IntelligenceRevisions_RecordCount", "[RecordCount] >= 0");
        });
        revision.HasKey(x => x.Id).IsClustered(false);
        revision.Property(x => x.Id).ValueGeneratedNever();
        revision.Property(x => x.RowId).UseIdentityColumn();
        revision.HasIndex(x => x.RowId).IsUnique().IsClustered();
        revision.HasAlternateKey(x => new { x.SourceId, x.Id }).IsClustered(false);
        revision.Property(x => x.UpstreamVersion).HasMaxLength(128).IsRequired();
        revision.Property(x => x.SchemaVersion).HasMaxLength(128).IsRequired();
        revision.Property(x => x.MappingVersion).HasMaxLength(128).IsRequired();
        revision.Property(x => x.LicenseEvidence).HasMaxLength(1024).IsRequired();
        revision.Property(x => x.Digest).HasColumnType("binary(32)").IsRequired();
        revision.Property(x => x.Artifact).IsRequired();
        revision.HasIndex(x => new { x.SourceId, x.Digest, x.MappingVersion }).IsUnique();
        revision.HasOne<IntelligenceSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.NoAction);
        var activation = model.Entity<IntelligenceActivation>();
        activation.ToTable("IntelligenceActivations");
        activation.HasKey(x => x.SourceId).IsClustered();
        activation.Property(x => x.SourceId).ValueGeneratedNever();
        activation.Property(x => x.Version).IsRowVersion();
        activation.HasOne<IntelligenceSource>().WithOne().HasForeignKey<IntelligenceActivation>(x => x.SourceId)
            .OnDelete(DeleteBehavior.NoAction);
        activation.HasOne<IntelligenceRevision>().WithMany().HasForeignKey(x => new { x.SourceId, x.RevisionId })
            .HasPrincipalKey(x => new { x.SourceId, x.Id }).OnDelete(DeleteBehavior.NoAction);

        var cache = model.Entity<IntelligenceCache>();
        cache.ToTable("IntelligenceCaches");
        cache.HasKey(x => x.SourceId).IsClustered();
        cache.Property(x => x.SourceId).ValueGeneratedNever();
        cache.Property(x => x.ETag).HasMaxLength(512);
        cache.HasOne<IntelligenceSource>().WithOne().HasForeignKey<IntelligenceCache>(x => x.SourceId)
            .OnDelete(DeleteBehavior.NoAction);
        cache.HasOne<IntelligenceRevision>().WithMany().HasForeignKey(x => new { x.SourceId, x.RevisionId })
            .HasPrincipalKey(x => new { x.SourceId, x.Id }).OnDelete(DeleteBehavior.NoAction);

        var history = model.Entity<IntelligenceActivationEvent>();
        history.ToTable("IntelligenceActivationEvents", t => t.HasCheckConstraint("CK_IntelligenceActivationEvents_Actor", "([Scheduled] = 1 AND [ActorId] IS NULL) OR ([Scheduled] = 0 AND [ActorId] IS NOT NULL AND [ActorId] <> '00000000-0000-0000-0000-000000000000')"));
        history.HasKey(x => x.RowId).IsClustered();
        history.Property(x => x.RowId).UseIdentityColumn();
        history.HasIndex(x => new { x.SourceId, x.OccurredAt, x.RowId });
        history.HasOne<IntelligenceRevision>().WithMany().HasForeignKey(x => new { x.SourceId, x.RevisionId })
            .HasPrincipalKey(x => new { x.SourceId, x.Id }).OnDelete(DeleteBehavior.NoAction);
        history.HasOne<IntelligenceRevision>().WithMany().HasForeignKey(x => new { x.SourceId, x.PreviousRevisionId })
            .HasPrincipalKey(x => new { x.SourceId, x.Id }).OnDelete(DeleteBehavior.NoAction);

        var kev = model.Entity<KevRecord>();
        kev.ToTable("KevRecords");
        kev.HasKey(x => x.RowId).IsClustered();
        kev.Property(x => x.RowId).UseIdentityColumn();
        kev.Property(x => x.CveId).HasMaxLength(28).IsUnicode(false).IsRequired();
        kev.Property(x => x.VendorProject).IsRequired();
        kev.Property(x => x.Product).IsRequired();
        kev.Property(x => x.VulnerabilityName).IsRequired();
        kev.Property(x => x.Description).IsRequired();
        kev.Property(x => x.RequiredAction).IsRequired();
        kev.HasIndex(x => new { x.RevisionId, x.CveId }).IsUnique();
        kev.HasOne<IntelligenceRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.NoAction);
    }
}
