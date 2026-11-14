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
            t.HasCheckConstraint("CK_IntelligenceSources_Settings", "[UpdateIntervalSeconds] BETWEEN 300 AND 604800 AND [StaleAfterSeconds] BETWEEN 3600 AND 2592000 AND [MaximumBytes] BETWEEN 1024 AND 1073741824 AND [MaximumRecords] BETWEEN 1 AND 1000000 AND ([Key] = 'github-reviewed' OR [MaximumBytes] <= 67108864)");
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
        source.Property(x => x.ProtectedCredential).HasMaxLength(8192);
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

        var import = model.Entity<AdvisoryImport>();
        import.ToTable("AdvisoryImports", t =>
        {
            t.HasCheckConstraint("CK_AdvisoryImports_Values", "[Id] <> '00000000-0000-0000-0000-000000000000' AND [WindowStart] <= [WindowEnd] AND [Phase] BETWEEN 1 AND 5 AND [PageCount] BETWEEN 0 AND 100000 AND [RecordCount] BETWEEN 0 AND 100000 AND [TotalBytes] BETWEEN 0 AND 1073741824");
            t.HasCheckConstraint("CK_AdvisoryImports_Base", "([BaseRevisionId] IS NULL AND [BaseActivationVersion] IS NULL) OR ([BaseRevisionId] IS NOT NULL AND [BaseActivationVersion] IS NOT NULL)");
            t.HasCheckConstraint("CK_AdvisoryImports_RateLimitAttempts", "[RateLimitAttempts] BETWEEN 0 AND 20");
            t.HasCheckConstraint("CK_AdvisoryImports_RestartCount", "[RestartCount] BETWEEN 0 AND 3");
            t.HasCheckConstraint("CK_AdvisoryImports_Reconciliation", "[ReconciliationPhase] BETWEEN 1 AND 4 AND [ReconciliationRecordCount] BETWEEN 0 AND 100000");
            t.HasCheckConstraint("CK_AdvisoryImports_FailureCode", "[FailureCode] IS NULL OR [FailureCode] IN ('fetch-failed','invalid-response','request-timeout','cursor-rejected','limit-exceeded','invalid-base','credential-unavailable')");
        });
        import.HasKey(x => x.Id).IsClustered(false);
        import.Property(x => x.Id).ValueGeneratedNever();
        import.Property(x => x.RowId).UseIdentityColumn();
        import.HasIndex(x => x.RowId).IsUnique().IsClustered();
        import.Property(x => x.SourceVersion).HasColumnType("binary(8)").IsRequired();
        import.Property(x => x.ResultSourceVersion).HasColumnType("binary(8)");
        import.Property(x => x.BaseActivationVersion).HasColumnType("binary(8)");
        import.Property(x => x.Cursor).HasMaxLength(1024).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        import.Property(x => x.FailureCode).HasMaxLength(32).IsUnicode(false);
        import.Property(x => x.Version).IsRowVersion();
        import.Property(x => x.ReconciliationPhase).HasDefaultValue(1);
        import.HasIndex(x => new { x.SourceId, x.StartedAt });
        import.HasIndex(x => new { x.SourceId, x.NotBefore }).HasFilter("[NotBefore] IS NOT NULL");
        import.HasIndex(x => x.SourceId).IsUnique().HasFilter("[Phase] < 4");
        import.HasOne<IntelligenceSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.NoAction);
        import.HasOne<IntelligenceRevision>().WithMany().HasForeignKey(x => new { x.SourceId, x.BaseRevisionId })
            .HasPrincipalKey(x => new { x.SourceId, x.Id }).OnDelete(DeleteBehavior.NoAction);
        import.HasOne<IntelligenceRevision>().WithMany().HasForeignKey(x => new { x.SourceId, x.ResultRevisionId })
            .HasPrincipalKey(x => new { x.SourceId, x.Id }).OnDelete(DeleteBehavior.NoAction);

        var page = model.Entity<AdvisoryImportPage>();
        page.ToTable("AdvisoryImportPages", t => t.HasCheckConstraint("CK_AdvisoryImportPages_Values", "[Number] > 0 AND [Phase] BETWEEN 1 AND 2 AND DATALENGTH([Payload]) BETWEEN 1 AND 2097152"));
        page.HasKey(x => x.RowId).IsClustered();
        page.Property(x => x.RowId).UseIdentityColumn();
        page.Property(x => x.Digest).HasColumnType("binary(32)").IsRequired();
        page.Property(x => x.Payload).IsRequired();
        page.Property(x => x.NextCursor).HasMaxLength(1024).IsUnicode(false).UseCollation("Latin1_General_100_BIN2");
        page.HasAlternateKey(x => new { x.ImportId, x.RowId }).IsClustered(false);
        page.HasIndex(x => new { x.ImportId, x.Number }).IsUnique();
        page.HasIndex(x => new { x.ImportId, x.Reconciliation, x.Phase, x.NextCursor });
        page.HasOne<AdvisoryImport>().WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.NoAction);

        var entry = model.Entity<AdvisoryImportEntry>();
        entry.ToTable("AdvisoryImportEntries", t => t.HasCheckConstraint("CK_AdvisoryImportEntries_Ordinal", "[Ordinal] BETWEEN 0 AND 99"));
        entry.HasKey(x => x.RowId).IsClustered();
        entry.Property(x => x.RowId).UseIdentityColumn();
        entry.Property(x => x.AdvisoryId).HasMaxLength(19).IsUnicode(false).IsRequired();
        entry.Property(x => x.Digest).HasColumnType("binary(32)").IsRequired();
        entry.HasAlternateKey(x => new { x.RowId, x.AdvisoryId }).IsClustered(false);
        entry.HasIndex(x => new { x.ImportId, x.Reconciliation, x.AdvisoryId }).IsUnique();
        entry.HasOne<AdvisoryImportPage>().WithMany().HasForeignKey(x => new { x.ImportId, x.PageRowId })
            .HasPrincipalKey(x => new { x.ImportId, x.RowId }).OnDelete(DeleteBehavior.NoAction);

        var advisoryRevision = model.Entity<AdvisoryRevisionEntry>();
        advisoryRevision.ToTable("AdvisoryRevisionEntries");
        advisoryRevision.HasKey(x => x.RowId).IsClustered();
        advisoryRevision.Property(x => x.RowId).UseIdentityColumn();
        advisoryRevision.Property(x => x.AdvisoryId).HasMaxLength(19).IsUnicode(false).IsRequired();
        advisoryRevision.HasIndex(x => new { x.RevisionId, x.AdvisoryId }).IsUnique();
        advisoryRevision.HasOne<IntelligenceRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.NoAction);
        advisoryRevision.HasOne<AdvisoryImportEntry>().WithMany().HasForeignKey(x => new { x.EntryRowId, x.AdvisoryId })
            .HasPrincipalKey(x => new { x.RowId, x.AdvisoryId }).OnDelete(DeleteBehavior.NoAction);

        var sigma = model.Entity<SigmaRule>();
        sigma.ToTable("SigmaRules", t => t.HasCheckConstraint("CK_SigmaRules_Content", "DATALENGTH([Content]) BETWEEN 1 AND 1048576"));
        sigma.HasKey(x => x.RowId).IsClustered();
        sigma.Property(x => x.RowId).UseIdentityColumn();
        sigma.Property(x => x.Path).HasMaxLength(512).UseCollation("Latin1_General_100_BIN2").IsRequired();
        sigma.Property(x => x.RuleId).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2");
        sigma.Property(x => x.Title).HasMaxLength(256).IsRequired();
        sigma.Property(x => x.Author).HasMaxLength(1024);
        sigma.Property(x => x.Status).HasMaxLength(128);
        sigma.Property(x => x.License).HasMaxLength(256);
        sigma.Property(x => x.Product).HasMaxLength(128);
        sigma.Property(x => x.Category).HasMaxLength(128);
        sigma.Property(x => x.Service).HasMaxLength(128);
        sigma.Property(x => x.Digest).HasColumnType("binary(32)").IsRequired();
        sigma.Property(x => x.Content).IsRequired();
        sigma.HasIndex(x => new { x.RevisionId, x.Path }).IsUnique();
        sigma.HasIndex(x => new { x.RevisionId, x.RuleId }).IsUnique().HasFilter("[RuleId] IS NOT NULL");
        sigma.HasOne<IntelligenceRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.NoAction);
    }
}
