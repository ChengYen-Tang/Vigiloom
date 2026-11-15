using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Domain.Detection;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;

namespace Vigiloom.Modules.SecurityAnalysis.Persistence;

internal static class DetectionModel
{
    internal static void Configure(ModelBuilder model)
    {
        var test = model.Entity<SigmaRuleTest>();
        test.ToTable("SigmaRuleTests");
        test.HasKey(x => x.Id).IsClustered(false);
        test.Property(x => x.Id).ValueGeneratedNever();
        test.Property(x => x.RowId).UseIdentityColumn();
        test.HasIndex(x => x.RowId).IsUnique().IsClustered();
        test.Property(x => x.Path).HasMaxLength(512).UseCollation("Latin1_General_100_BIN2").IsRequired();
        test.Property(x => x.Digest).HasMaxLength(64).IsUnicode(false).IsRequired();
        test.Property(x => x.ActivationVersion).HasMaxLength(128).IsUnicode(false).IsRequired();
        test.Property(x => x.MatcherVersion).HasMaxLength(64).IsUnicode(false).IsRequired();
        test.Property(x => x.SamplesJson).IsRequired();
        test.HasAlternateKey(x => new { x.Id, x.SourceId }).IsClustered(false);
        test.HasIndex(x => new { x.SourceId, x.Path, x.TestedAt, x.RowId });
        test.HasOne<IntelligenceRevision>().WithMany().HasForeignKey(x => new { x.SourceId, x.RevisionId })
            .HasPrincipalKey(x => new { x.SourceId, x.Id }).OnDelete(DeleteBehavior.NoAction);

        var activation = model.Entity<SigmaRuleActivation>();
        activation.ToTable("SigmaRuleActivations");
        activation.HasKey(x => x.Id).IsClustered(false);
        activation.Property(x => x.Id).ValueGeneratedNever();
        activation.Property(x => x.RowId).UseIdentityColumn();
        activation.HasIndex(x => x.RowId).IsUnique().IsClustered();
        activation.Property(x => x.Path).HasMaxLength(512).UseCollation("Latin1_General_100_BIN2").IsRequired();
        activation.Property(x => x.Version).IsRowVersion();
        activation.HasIndex(x => new { x.SourceId, x.Path }).IsUnique();
        activation.HasOne<SigmaRuleTest>().WithMany().HasForeignKey(x => new { x.TestId, x.SourceId })
            .HasPrincipalKey(x => new { x.Id, x.SourceId }).OnDelete(DeleteBehavior.NoAction);

        var result = model.Entity<SigmaDetectionResult>();
        result.ToTable("SigmaDetectionResults", t =>
        {
            t.HasCheckConstraint("CK_SigmaDetectionResults_Result", "[Result] IN (1, 2)");
            t.HasCheckConstraint("CK_SigmaDetectionResults_Correlation", "([CorrelationCount] IS NULL AND [CorrelationFirstAt] IS NULL AND [CorrelationLastAt] IS NULL) OR ([Result] = 1 AND [CorrelationCount] IS NOT NULL AND [CorrelationCount] > 0 AND [CorrelationFirstAt] IS NOT NULL AND [CorrelationLastAt] IS NOT NULL AND [CorrelationFirstAt] <= [CorrelationLastAt])");
        });
        result.HasKey(x => x.RowId).IsClustered();
        result.Property(x => x.RowId).UseIdentityColumn();
        result.Property(x => x.ActivationVersion).HasColumnType("binary(8)").IsRequired();
        result.HasIndex(x => new { x.AgentId, x.MessageId, x.TestId, x.ActivationVersion }).IsUnique();
        result.HasIndex(x => new { x.DeviceId, x.ReceivedAt, x.RowId });
        result.HasOne<SigmaRuleActivation>().WithMany().HasForeignKey(x => x.ActivationId).OnDelete(DeleteBehavior.NoAction);
        result.HasOne<SigmaRuleTest>().WithMany().HasForeignKey(x => x.TestId).OnDelete(DeleteBehavior.NoAction);

        var correlationEvent = model.Entity<SigmaCorrelationEvent>();
        correlationEvent.ToTable("SigmaCorrelationEvents", table => table.HasCheckConstraint(
            "CK_SigmaCorrelationEvents_DeviceGeneration", "[DeviceGeneration] >= 0"));
        correlationEvent.HasKey(x => x.RowId).IsClustered();
        correlationEvent.Property(x => x.RowId).UseIdentityColumn();
        correlationEvent.Property(x => x.ActivationVersion).HasColumnType("binary(8)").IsRequired();
        correlationEvent.HasIndex(x => new { x.TestId, x.ActivationVersion, x.AgentId, x.MessageId }).IsUnique();
        correlationEvent.HasIndex(x => new { x.TestId, x.ActivationVersion, x.OccurredAt, x.RowId });
        correlationEvent.HasOne<SigmaRuleTest>().WithMany().HasForeignKey(x => x.TestId).OnDelete(DeleteBehavior.NoAction);

        var coverage = model.Entity<SigmaCorrelationCoverage>();
        coverage.ToTable("SigmaCorrelationCoverage");
        coverage.HasKey(x => x.RowId).IsClustered();
        coverage.Property(x => x.RowId).UseIdentityColumn();
        coverage.Property(x => x.ActivationVersion).HasColumnType("binary(8)").IsRequired();
        coverage.HasIndex(x => new { x.TestId, x.ActivationVersion }).IsUnique();
        coverage.HasOne<SigmaRuleTest>().WithMany().HasForeignKey(x => x.TestId).OnDelete(DeleteBehavior.NoAction);

        var gap = model.Entity<SigmaEventGap>();
        gap.ToTable("SigmaEventGaps", t => t.HasCheckConstraint("CK_SigmaEventGaps_Kind", "[Kind] IN (1, 2)"));
        gap.HasKey(x => x.RowId).IsClustered();
        gap.Property(x => x.RowId).UseIdentityColumn();
        gap.HasIndex(x => new { x.AgentId, x.MessageId, x.Kind }).IsUnique();
        gap.HasIndex(x => new { x.DeviceId, x.RecordedAt, x.RowId });
    }
}
