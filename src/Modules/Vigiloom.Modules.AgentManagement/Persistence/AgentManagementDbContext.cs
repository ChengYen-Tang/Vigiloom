using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AgentManagement.Domain;

namespace Vigiloom.Modules.AgentManagement.Persistence;

public sealed class AgentManagementDbContext(DbContextOptions<AgentManagementDbContext> options) : DbContext(options)
{
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentReplayPolicy> ReplayPolicies => Set<AgentReplayPolicy>();
    public DbSet<AgentReplayGeneration> ReplayGenerations => Set<AgentReplayGeneration>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();
    public DbSet<AgentAudit> Audits => Set<AgentAudit>();
    public DbSet<ReceivedBatch> ReceivedBatches => Set<ReceivedBatch>();
    public DbSet<RuntimeObservation> RuntimeObservations => Set<RuntimeObservation>();
    public DbSet<HostMetric> HostMetrics => Set<HostMetric>();
    public DbSet<AgentSetting> Settings => Set<AgentSetting>();
    public DbSet<AgentSettingAudit> SettingAudits => Set<AgentSettingAudit>();
    public DbSet<AgentQueueCapacitySetting> QueueCapacitySettings => Set<AgentQueueCapacitySetting>();
    public DbSet<AgentQueueCapacityAudit> QueueCapacityAudits => Set<AgentQueueCapacityAudit>();
    public DbSet<AgentMetricSetting> MetricSettings => Set<AgentMetricSetting>();
    public DbSet<AgentMetricSettingAudit> MetricSettingAudits => Set<AgentMetricSettingAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("agent");
        var agent = modelBuilder.Entity<Agent>();
        agent.ToTable("Agents", table =>
        {
            table.HasCheckConstraint("CK_Agents_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_Agents_HeartbeatSafety", "[HeartbeatSafetyIntervalSeconds] BETWEEN 10 AND 300");
            table.HasCheckConstraint("CK_Agents_ReplayIssue", "([LastReplayIssue] IS NULL AND [LastReplayIssueAt] IS NULL) OR ([LastReplayIssue] IS NOT NULL AND [LastReplayIssue] IN (1, 2, 3) AND [LastReplayIssueAt] IS NOT NULL)");
            table.HasCheckConstraint("CK_Agents_InboxUsage", "([InboxReceiptCount] IS NULL AND [InboxReceiptPayloadBytes] IS NULL) OR ([InboxReceiptCount] >= 0 AND [InboxReceiptPayloadBytes] >= 0 AND [InboxReceiptCount] IS NOT NULL AND [InboxReceiptPayloadBytes] IS NOT NULL)");
        });
        agent.HasKey(x => x.Id).IsClustered(false);
        agent.Property(x => x.Id).ValueGeneratedNever();
        agent.Property(x => x.RowId).UseIdentityColumn();
        agent.HasIndex(x => x.RowId).IsUnique().IsClustered();
        agent.Property(x => x.Verifier).HasColumnType("binary(32)").IsRequired();
        agent.Property(x => x.PendingVerifier).HasColumnType("binary(32)");
        agent.Property(x => x.HeartbeatSafetyIntervalSeconds).HasDefaultValue(30);
        agent.Property(x => x.AppliedConfigurationRevision).HasMaxLength(64);
        agent.Property(x => x.QueueCapacityAppliedRevision).HasMaxLength(64);
        agent.HasIndex(x => x.DeviceId).IsUnique().HasFilter("[RevokedAt] IS NULL");

        var replay = modelBuilder.Entity<AgentReplayPolicy>();
        replay.ToTable("ReplayPolicies", table =>
        {
            table.HasCheckConstraint("CK_ReplayPolicies_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_ReplayPolicies_Scope", "[Scope] = 1");
            table.HasCheckConstraint("CK_ReplayPolicies_ReplayWindowSeconds", "[ReplayWindowSeconds] BETWEEN 1 AND 2592000");
            table.HasCheckConstraint("CK_ReplayPolicies_ReceiptRetentionDays", "[ReceiptRetentionDays] BETWEEN 1 AND 3650");
            table.HasCheckConstraint("CK_ReplayPolicies_WindowWithinRetention", "CAST([ReceiptRetentionDays] AS bigint) * 86400 >= CAST([ReplayWindowSeconds] AS bigint) + 300");
            table.HasCheckConstraint("CK_ReplayPolicies_InboxTimes", "([CleanupCutoverAt] IS NULL AND [InboxSettingsChangedAt] IS NULL) OR ([Phase] = 2 AND [CleanupCutoverAt] IS NOT NULL AND [InboxSettingsChangedAt] IS NOT NULL AND [InboxSettingsChangedAt] >= [CleanupCutoverAt] AND [CleanupCutoverAt] >= [EnforcedAt])");
            table.HasCheckConstraint("CK_ReplayPolicies_LegacyDeadline", "([CleanupCutoverAt] IS NULL AND [LegacyReceiptsRetainUntil] IS NULL) OR ([CleanupCutoverAt] IS NOT NULL AND [LegacyReceiptsRetainUntil] IS NOT NULL AND [LegacyReceiptsRetainUntil] >= [CleanupCutoverAt])");
            table.HasCheckConstraint("CK_ReplayPolicies_Phase", "([Phase] = 0 AND [FreshInstallation] = 0 AND [ActivatedAt] IS NULL AND [GraceUntil] IS NULL AND [EnforcedAt] IS NULL) OR ([Phase] = 1 AND [FreshInstallation] = 0 AND [ActivatedAt] IS NOT NULL AND [GraceUntil] IS NOT NULL AND [GraceUntil] > [ActivatedAt] AND [EnforcedAt] IS NULL) OR ([Phase] = 2 AND [ActivatedAt] IS NOT NULL AND [EnforcedAt] IS NOT NULL AND (([FreshInstallation] = 1 AND [GraceUntil] IS NULL AND [EnforcedAt] = [ActivatedAt]) OR ([FreshInstallation] = 0 AND [GraceUntil] IS NOT NULL AND [GraceUntil] > [ActivatedAt] AND [EnforcedAt] >= [GraceUntil])))");
        });
        replay.HasKey(x => x.Id).IsClustered(false);
        replay.Property(x => x.Id).ValueGeneratedNever();
        replay.Property(x => x.RowId).UseIdentityColumn();
        replay.HasIndex(x => x.RowId).IsUnique().IsClustered();
        replay.HasIndex(x => x.Scope).IsUnique();
        replay.Property(x => x.Version).IsRowVersion();
        replay.Property(x => x.ReplayWindowSeconds).HasDefaultValue(86400);
        replay.Property(x => x.ReceiptRetentionDays).HasDefaultValue(7);

        var generation = modelBuilder.Entity<AgentReplayGeneration>();
        generation.ToTable("ReplayGenerations", table => table.HasCheckConstraint("CK_ReplayGenerations_WindowSeconds", "[WindowSeconds] BETWEEN 1 AND 2592000"));
        generation.HasKey(x => x.RowId).IsClustered();
        generation.Property(x => x.RowId).UseIdentityColumn();
        generation.HasIndex(x => new { x.PolicyId, x.ValidThrough }).IncludeProperties(x => x.WindowSeconds);
        generation.HasOne<AgentReplayPolicy>().WithMany().HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.NoAction);

        var enrollment = modelBuilder.Entity<Enrollment>();
        enrollment.ToTable("Enrollments", table => table.HasCheckConstraint("CK_Enrollments_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'"));
        enrollment.HasKey(x => x.Id).IsClustered(false);
        enrollment.Property(x => x.Id).ValueGeneratedNever();
        enrollment.Property(x => x.RowId).UseIdentityColumn();
        enrollment.HasIndex(x => x.RowId).IsUnique().IsClustered();
        enrollment.Property(x => x.Verifier).HasColumnType("binary(32)").IsRequired();
        enrollment.HasIndex(x => x.DeviceId);
        enrollment.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.NoAction);

        var audit = modelBuilder.Entity<AgentAudit>();
        audit.ToTable("AgentAudits");
        audit.HasKey(x => x.RowId).IsClustered();
        audit.Property(x => x.RowId).UseIdentityColumn();
        audit.Property(x => x.Action).HasMaxLength(32).IsRequired();
        audit.HasIndex(x => new { x.DeviceId, x.OccurredAt, x.RowId });
        audit.HasIndex(x => new { x.OccurredAt, x.RowId });
        audit.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.NoAction);

        var batch = modelBuilder.Entity<ReceivedBatch>();
        batch.ToTable("ReceivedBatches");
        batch.HasKey(x => x.RowId).IsClustered();
        batch.Property(x => x.RowId).UseIdentityColumn();
        batch.Property(x => x.Digest).HasColumnType("binary(32)").IsRequired();
        batch.HasIndex(x => new { x.AgentId, x.BatchId }).IsUnique();
        batch.HasIndex(x => new { x.AgentId, x.ReceivedAt, x.RowId });
        batch.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.NoAction);

        var observation = modelBuilder.Entity<RuntimeObservation>();
        observation.ToTable("RuntimeObservations");
        observation.HasKey(x => x.RowId).IsClustered();
        observation.Property(x => x.RowId).UseIdentityColumn();
        observation.Property(x => x.OperatingSystem).HasMaxLength(256).IsRequired();
        observation.Property(x => x.Architecture).HasMaxLength(32).IsRequired();
        observation.Property(x => x.RuntimeVersion).HasMaxLength(128).IsRequired();
        observation.HasIndex(x => new { x.AgentId, x.MessageId }).IsUnique();
        observation.HasIndex(x => new { x.AgentId, x.OccurredAt, x.RowId });
        observation.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.NoAction);

        var metric = modelBuilder.Entity<HostMetric>();
        metric.ToTable("HostMetrics");
        metric.HasKey(x => x.RowId).IsClustered();
        metric.Property(x => x.RowId).UseIdentityColumn();
        metric.Property(x => x.Source).HasMaxLength(128).IsRequired();
        metric.Property(x => x.Dimension).HasMaxLength(256);
        metric.HasIndex(x => new { x.AgentId, x.MessageId }).IsUnique();
        metric.HasIndex(x => new { x.AgentId, x.OccurredAt, x.RowId });
        metric.HasIndex(x => new { x.AgentId, x.Kind, x.OccurredAt, x.RowId });
        metric.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.NoAction);

        var setting = modelBuilder.Entity<AgentSetting>();
        setting.ToTable("Settings", table => table.HasCheckConstraint("CK_AgentSettings_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'"));
        setting.HasKey(x => x.Id).IsClustered(false);
        setting.Property(x => x.Id).ValueGeneratedNever();
        setting.Property(x => x.RowId).UseIdentityColumn();
        setting.HasIndex(x => x.RowId).IsUnique().IsClustered();
        setting.HasIndex(x => x.DeviceId).IsUnique().HasFilter(null);
        setting.Property(x => x.Version).IsRowVersion();

        var settingAudit = modelBuilder.Entity<AgentSettingAudit>();
        settingAudit.ToTable("SettingAudits");
        settingAudit.HasKey(x => x.RowId).IsClustered();
        settingAudit.Property(x => x.RowId).UseIdentityColumn();
        settingAudit.Property(x => x.EffectiveRevision).HasMaxLength(64).IsRequired();
        settingAudit.Property(x => x.ChangeVersion).HasColumnType("binary(8)").IsRequired();
        settingAudit.HasIndex(x => new { x.DeviceId, x.OccurredAt, x.RowId });
        settingAudit.HasIndex(x => new { x.OccurredAt, x.RowId });
        settingAudit.HasOne<AgentSetting>().WithMany().HasForeignKey(x => x.SettingId).OnDelete(DeleteBehavior.NoAction);

        var capacity = modelBuilder.Entity<AgentQueueCapacitySetting>();
        capacity.ToTable("QueueCapacitySettings", table => table.HasCheckConstraint("CK_QueueCapacitySettings_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'"));
        capacity.HasKey(x => x.Id).IsClustered(false);
        capacity.Property(x => x.Id).ValueGeneratedNever();
        capacity.Property(x => x.RowId).UseIdentityColumn();
        capacity.HasIndex(x => x.RowId).IsUnique().IsClustered();
        capacity.HasIndex(x => x.DeviceId).IsUnique().HasFilter(null);
        capacity.Property(x => x.Version).IsRowVersion();

        var capacityAudit = modelBuilder.Entity<AgentQueueCapacityAudit>();
        capacityAudit.ToTable("QueueCapacityAudits");
        capacityAudit.HasKey(x => x.RowId).IsClustered();
        capacityAudit.Property(x => x.RowId).UseIdentityColumn();
        capacityAudit.Property(x => x.EffectiveRevision).HasMaxLength(64).IsRequired();
        capacityAudit.Property(x => x.ChangeVersion).HasColumnType("binary(8)").IsRequired();
        capacityAudit.HasIndex(x => new { x.DeviceId, x.OccurredAt, x.RowId });
        capacityAudit.HasIndex(x => new { x.OccurredAt, x.RowId });
        capacityAudit.HasOne<AgentQueueCapacitySetting>().WithMany().HasForeignKey(x => x.SettingId).OnDelete(DeleteBehavior.NoAction);

        var metricSetting = modelBuilder.Entity<AgentMetricSetting>();
        metricSetting.ToTable("MetricSettings", table => table.HasCheckConstraint("CK_MetricSettings_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'"));
        metricSetting.HasKey(x => x.Id).IsClustered(false);
        metricSetting.Property(x => x.Id).ValueGeneratedNever();
        metricSetting.Property(x => x.RowId).UseIdentityColumn();
        metricSetting.HasIndex(x => x.RowId).IsUnique().IsClustered();
        metricSetting.HasIndex(x => x.DeviceId).IsUnique().HasFilter(null);
        metricSetting.Property(x => x.Version).IsRowVersion();

        var metricAudit = modelBuilder.Entity<AgentMetricSettingAudit>();
        metricAudit.ToTable("MetricSettingAudits");
        metricAudit.HasKey(x => x.RowId).IsClustered();
        metricAudit.Property(x => x.RowId).UseIdentityColumn();
        metricAudit.Property(x => x.EffectiveCpuMillisecondsPerMinute).HasDefaultValue(3000);
        metricAudit.Property(x => x.EffectiveWorkingSetLimitMiB).HasDefaultValue(512);
        metricAudit.Property(x => x.EffectiveProcessCollectionBudgetMs).HasDefaultValue(2000);
        metricAudit.Property(x => x.EffectiveIoLimitMiBPerMinute).HasDefaultValue(64);
        metricAudit.Property(x => x.EffectiveRevision).HasMaxLength(64).IsRequired();
        metricAudit.Property(x => x.ChangeVersion).HasColumnType("binary(8)").IsRequired();
        metricAudit.HasIndex(x => new { x.DeviceId, x.OccurredAt, x.RowId });
        metricAudit.HasIndex(x => new { x.OccurredAt, x.RowId });
        metricAudit.HasOne<AgentMetricSetting>().WithMany().HasForeignKey(x => x.SettingId).OnDelete(DeleteBehavior.NoAction);
    }
}
