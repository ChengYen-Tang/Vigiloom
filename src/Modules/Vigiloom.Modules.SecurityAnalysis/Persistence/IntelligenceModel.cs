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
            t.HasCheckConstraint("CK_IntelligenceSources_Settings", "[UpdateIntervalSeconds] BETWEEN 300 AND 604800 AND [StaleAfterSeconds] BETWEEN 3600 AND 2592000 AND [MaximumBytes] BETWEEN 1024 AND 1073741824 AND [MaximumRecords] BETWEEN 1 AND 1000000 AND ([Key] IN ('github-reviewed', 'msrc-cvrf') OR ([Key] IN ('debian-security', 'ubuntu-osv') AND [MaximumBytes] <= 268435456) OR [MaximumBytes] <= 67108864)");
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

        var debian = model.Entity<DebianAdvisory>();
        debian.ToTable("DebianAdvisories", t =>
        {
            t.HasCheckConstraint("CK_DebianAdvisories_State", "[State] BETWEEN 0 AND 3");
            t.HasCheckConstraint("CK_DebianAdvisories_Fix", "([State] = 1 AND [FixedVersion] IS NOT NULL) OR ([State] <> 1 AND [FixedVersion] IS NULL)");
        });
        debian.HasKey(x => x.RowId).IsClustered();
        debian.Property(x => x.RowId).UseIdentityColumn();
        debian.Property(x => x.SourcePackage).HasMaxLength(256).UseCollation("Latin1_General_100_BIN2").IsRequired();
        debian.Property(x => x.AdvisoryId).HasMaxLength(256).UseCollation("Latin1_General_100_BIN2").IsRequired();
        debian.Property(x => x.Release).HasMaxLength(256).UseCollation("Latin1_General_100_BIN2").IsRequired();
        debian.Property(x => x.FixedVersion).HasMaxLength(2048);
        debian.HasIndex(x => new { x.RevisionId, x.Release, x.SourcePackage, x.AdvisoryId }).IsUnique();
        debian.HasOne<IntelligenceRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.NoAction);

        var ubuntuDocument = model.Entity<UbuntuDocument>();
        ubuntuDocument.ToTable("UbuntuDocuments", t => t.HasCheckConstraint("CK_UbuntuDocuments_Length", "[OriginalLength] BETWEEN 1 AND 8388608 AND DATALENGTH([Compressed]) > 0"));
        ubuntuDocument.HasKey(x => x.RowId).IsClustered();
        ubuntuDocument.Property(x => x.RowId).UseIdentityColumn();
        ubuntuDocument.Property(x => x.Digest).HasColumnType("binary(32)").IsRequired();
        ubuntuDocument.Property(x => x.MappingVersion).HasMaxLength(64).IsUnicode(false).UseCollation("Latin1_General_100_BIN2").IsRequired();
        ubuntuDocument.Property(x => x.AdvisoryId).HasMaxLength(128).IsUnicode(false).UseCollation("Latin1_General_100_BIN2").IsRequired();
        ubuntuDocument.Property(x => x.Compressed).IsRequired();
        ubuntuDocument.HasAlternateKey(x => new { x.Digest, x.MappingVersion }).IsClustered(false);
        ubuntuDocument.HasAlternateKey(x => new { x.Digest, x.MappingVersion, x.AdvisoryId }).IsClustered(false);

        var ubuntuScope = model.Entity<UbuntuScope>();
        ubuntuScope.ToTable("UbuntuScopes");
        ubuntuScope.HasKey(x => x.RowId).IsClustered();
        ubuntuScope.Property(x => x.RowId).UseIdentityColumn();
        ubuntuScope.Property(x => x.Digest).HasColumnType("binary(32)").IsRequired();
        ubuntuScope.Property(x => x.MappingVersion).HasMaxLength(64).IsUnicode(false).UseCollation("Latin1_General_100_BIN2").IsRequired();
        ubuntuScope.Property(x => x.Ecosystem).HasMaxLength(256).UseCollation("Latin1_General_100_BIN2").IsRequired();
        ubuntuScope.Property(x => x.SourcePackage).HasMaxLength(256).UseCollation("Latin1_General_100_BIN2").IsRequired();
        ubuntuScope.HasIndex(x => new { x.Ecosystem, x.SourcePackage, x.Digest, x.MappingVersion }).IsUnique();
        ubuntuScope.HasOne<UbuntuDocument>().WithMany().HasForeignKey(x => new { x.Digest, x.MappingVersion })
            .HasPrincipalKey(x => new { x.Digest, x.MappingVersion }).OnDelete(DeleteBehavior.NoAction);

        var ubuntuRevision = model.Entity<UbuntuRevisionDocument>();
        ubuntuRevision.ToTable("UbuntuRevisionDocuments");
        ubuntuRevision.HasKey(x => new { x.RevisionId, x.AdvisoryId }).IsClustered();
        ubuntuRevision.Property(x => x.AdvisoryId).HasMaxLength(128).IsUnicode(false).UseCollation("Latin1_General_100_BIN2").IsRequired();
        ubuntuRevision.Property(x => x.Digest).HasColumnType("binary(32)").IsRequired();
        ubuntuRevision.Property(x => x.MappingVersion).HasMaxLength(64).IsUnicode(false).UseCollation("Latin1_General_100_BIN2").IsRequired();
        ubuntuRevision.HasIndex(x => new { x.Digest, x.MappingVersion, x.RevisionId });
        ubuntuRevision.HasIndex(x => new { x.AdvisoryId, x.RevisionId });
        ubuntuRevision.HasOne<IntelligenceRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.NoAction);
        ubuntuRevision.HasOne<UbuntuDocument>().WithMany().HasForeignKey(x => new { x.Digest, x.MappingVersion, x.AdvisoryId })
            .HasPrincipalKey(x => new { x.Digest, x.MappingVersion, x.AdvisoryId }).OnDelete(DeleteBehavior.NoAction);

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
        sigma.Property(x => x.ExclusionReason).HasMaxLength(64).IsUnicode(false);
        sigma.Property(x => x.Digest).HasColumnType("binary(32)").IsRequired();
        sigma.Property(x => x.Content).IsRequired();
        sigma.HasIndex(x => new { x.RevisionId, x.Path }).IsUnique();
        sigma.HasIndex(x => new { x.RevisionId, x.RuleId }).IsUnique().HasFilter("[RuleId] IS NOT NULL");
        sigma.HasOne<IntelligenceRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.NoAction);

        var sigmaManifest = model.Entity<SigmaManifest>();
        sigmaManifest.ToTable("SigmaManifests", t => t.HasCheckConstraint("CK_SigmaManifests_Count", "[IncludedCount] >= 0 AND [ExcludedCount] >= 0"));
        sigmaManifest.HasKey(x => x.RowId).IsClustered();
        sigmaManifest.Property(x => x.RowId).UseIdentityColumn();
        sigmaManifest.Property(x => x.PolicyVersion).HasMaxLength(64).IsUnicode(false).IsRequired();
        sigmaManifest.Property(x => x.Digest).HasColumnType("binary(32)").IsRequired();
        sigmaManifest.HasIndex(x => x.RevisionId).IsUnique();
        sigmaManifest.HasOne<IntelligenceRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.NoAction);

        var signature = model.Entity<SignatureFile>();
        signature.ToTable("SignatureFiles", t =>
        {
            t.HasCheckConstraint("CK_SignatureFiles_Content", "DATALENGTH([Content]) BETWEEN 1 AND 1048576");
            t.HasCheckConstraint("CK_SignatureFiles_Kind", "[Kind] BETWEEN 0 AND 2");
        });
        signature.HasKey(x => x.RowId).IsClustered();
        signature.Property(x => x.RowId).UseIdentityColumn();
        signature.Property(x => x.Path).HasMaxLength(512).UseCollation("Latin1_General_100_BIN2").IsRequired();
        signature.Property(x => x.Digest).HasColumnType("binary(32)").IsRequired();
        signature.Property(x => x.Content).IsRequired();
        signature.HasIndex(x => new { x.RevisionId, x.Path }).IsUnique();
        signature.HasOne<IntelligenceRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.NoAction);

        var ioc = model.Entity<IocEntry>();
        ioc.ToTable("IocEntries", t => t.HasCheckConstraint("CK_IocEntries_Line", "[LineNumber] > 0"));
        ioc.HasKey(x => x.RowId).IsClustered();
        ioc.Property(x => x.RowId).UseIdentityColumn();
        ioc.Property(x => x.Kind).HasMaxLength(32).IsUnicode(false).IsRequired();
        ioc.Property(x => x.Value).HasMaxLength(2048);
        ioc.Property(x => x.Reason).HasMaxLength(128).IsUnicode(false);
        ioc.HasIndex(x => new { x.SignatureFileRowId, x.LineNumber }).IsUnique();
        ioc.HasOne<SignatureFile>().WithMany().HasForeignKey(x => x.SignatureFileRowId).OnDelete(DeleteBehavior.NoAction);

        var msrc = model.Entity<MsrcDocument>();
        msrc.ToTable("MsrcDocuments");
        msrc.HasKey(x => x.RowId).IsClustered();
        msrc.Property(x => x.RowId).UseIdentityColumn();
        msrc.Property(x => x.DocumentId).HasMaxLength(16).IsUnicode(false).UseCollation("Latin1_General_100_BIN2").IsRequired();
        msrc.Property(x => x.Digest).HasColumnType("binary(32)").IsRequired();
        msrc.HasIndex(x => new { x.RevisionId, x.DocumentId }).IsUnique();
        msrc.HasOne<IntelligenceRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.NoAction);
        var msrcArtifact = model.Entity<MsrcArtifact>();
        msrcArtifact.ToTable("MsrcArtifacts", t => t.HasCheckConstraint("CK_MsrcArtifacts_Content", "DATALENGTH([Content]) BETWEEN 1 AND 67108864"));
        msrcArtifact.HasKey(x => x.RowId).IsClustered();
        msrcArtifact.Property(x => x.RowId).UseIdentityColumn();
        msrcArtifact.Property(x => x.Digest).HasColumnType("binary(32)").IsRequired();
        msrcArtifact.Property(x => x.Content).IsRequired();
        msrcArtifact.Property(x => x.IndexedCveCount).HasDefaultValue(-1).ValueGeneratedNever();
        msrcArtifact.Property(x => x.IndexedProductCount).HasDefaultValue(-1).ValueGeneratedNever();
        msrcArtifact.HasAlternateKey(x => x.Digest).IsClustered(false);
        msrc.HasOne<MsrcArtifact>().WithMany().HasForeignKey(x => x.Digest).HasPrincipalKey(x => x.Digest).OnDelete(DeleteBehavior.NoAction);
        var msrcCve = model.Entity<MsrcCveIndex>();
        msrcCve.ToTable("MsrcCveIndexes");
        msrcCve.HasKey(x => x.RowId).IsClustered();
        msrcCve.Property(x => x.RowId).UseIdentityColumn();
        msrcCve.Property(x => x.Digest).HasColumnType("binary(32)").IsRequired();
        msrcCve.Property(x => x.Cve).HasMaxLength(64).UseCollation("Latin1_General_100_BIN2").IsRequired();
        msrcCve.HasIndex(x => new { x.Digest, x.Cve }).IsUnique();
        msrcCve.HasIndex(x => new { x.Cve, x.Digest });
        msrcCve.HasOne<MsrcArtifact>().WithMany().HasForeignKey(x => x.Digest).HasPrincipalKey(x => x.Digest).OnDelete(DeleteBehavior.Cascade);

        var msrcProduct = model.Entity<MsrcProductIndex>();
        msrcProduct.ToTable("MsrcProductIndexes");
        msrcProduct.HasKey(x => x.RowId).IsClustered();
        msrcProduct.Property(x => x.RowId).UseIdentityColumn();
        msrcProduct.Property(x => x.Digest).HasColumnType("binary(32)").IsRequired();
        msrcProduct.Property(x => x.ProductHash).HasColumnType("binary(32)").IsRequired();
        msrcProduct.Property(x => x.ProductName).HasMaxLength(512).UseCollation("Latin1_General_100_BIN2").IsRequired();
        msrcProduct.Property(x => x.Cve).HasMaxLength(64).UseCollation("Latin1_General_100_BIN2").IsRequired();
        msrcProduct.HasIndex(x => new { x.Digest, x.ProductHash, x.Cve }).IsUnique();
        msrcProduct.HasIndex(x => new { x.ProductHash, x.Digest, x.Cve });
        msrcProduct.HasOne<MsrcArtifact>().WithMany().HasForeignKey(x => x.Digest).HasPrincipalKey(x => x.Digest).OnDelete(DeleteBehavior.Cascade);

        var epss = model.Entity<EpssScoreRecord>();
        epss.ToTable("EpssScores", t => t.HasCheckConstraint("CK_EpssScores_Ranges", "[Probability] BETWEEN 0 AND 1 AND [Percentile] BETWEEN 0 AND 1"));
        epss.HasKey(x => x.RowId).IsClustered();
        epss.Property(x => x.RowId).UseIdentityColumn();
        epss.Property(x => x.Cve).HasMaxLength(28).IsUnicode(false).UseCollation("Latin1_General_100_BIN2").IsRequired();
        epss.Property(x => x.Probability).HasPrecision(10, 9);
        epss.Property(x => x.Percentile).HasPrecision(10, 9);
        epss.HasIndex(x => new { x.RevisionId, x.Cve }).IsUnique();
        epss.HasIndex(x => new { x.RevisionId, x.Probability, x.Cve }).IsDescending(false, true, false);
        epss.HasOne<IntelligenceRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.NoAction);

        var offline = model.Entity<OfflineImport>();
        offline.ToTable("OfflineImports", t => t.HasCheckConstraint("CK_OfflineImports_Actor", "[ActorId] <> '00000000-0000-0000-0000-000000000000'"));
        offline.HasKey(x => x.RowId).IsClustered();
        offline.Property(x => x.RowId).UseIdentityColumn();
        offline.HasIndex(x => new { x.SourceId, x.RevisionId, x.ImportedAt });
        offline.HasOne<IntelligenceRevision>().WithMany().HasForeignKey(x => new { x.SourceId, x.RevisionId })
            .HasPrincipalKey(x => new { x.SourceId, x.Id }).OnDelete(DeleteBehavior.NoAction);
    }
}
