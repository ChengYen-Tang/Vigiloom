using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Vigiloom.Modules.AgentManagement.Domain;

namespace Vigiloom.Modules.AgentManagement.Persistence;

/// <summary>Host-owned observations and receipts; Agent identity belongs to Resources.</summary>
public sealed class HostMonitoringDbContext(DbContextOptions<HostMonitoringDbContext> options) : DbContext(options)
{
    public const string MigrationHistory = "__EFMigrationsHistory_HostMonitoring";
    public DbSet<HostInboxState> InboxStates => Set<HostInboxState>();
    public DbSet<AgentReplayPolicy> ReplayPolicies => Set<AgentReplayPolicy>();
    public DbSet<AgentReplayGeneration> ReplayGenerations => Set<AgentReplayGeneration>();
    public DbSet<ReceivedBatch> ReceivedBatches => Set<ReceivedBatch>();
    public DbSet<HostLogMessageId> HostLogMessageIds => Set<HostLogMessageId>();
    public DbSet<RuntimeObservation> RuntimeObservations => Set<RuntimeObservation>();
    public DbSet<HostMetric> HostMetrics => Set<HostMetric>();
    public DbSet<HostMetricRollup> HostMetricRollups => Set<HostMetricRollup>();
    public DbSet<HostMetricRetentionSetting> HostMetricRetentionSettings => Set<HostMetricRetentionSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("agent");
        var inbox = modelBuilder.Entity<HostInboxState>();
        inbox.ToTable("HostInboxStates");
        inbox.HasKey(x => x.Id).IsClustered(false);
        inbox.Property(x => x.Id).ValueGeneratedNever();
        inbox.Property(x => x.RowId).UseIdentityColumn();
        inbox.HasIndex(x => x.RowId).IsUnique().IsClustered();
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

        var batch = modelBuilder.Entity<ReceivedBatch>();
        batch.ToTable("ReceivedBatches");
        batch.HasKey(x => x.RowId).IsClustered();
        batch.Property(x => x.RowId).UseIdentityColumn();
        batch.Property(x => x.Digest).HasColumnType("binary(32)").IsRequired();
        batch.Property<bool>("PendingDelivery").HasDefaultValue(false);
        batch.HasIndex(x => new { x.AgentId, x.BatchId }).IsUnique();
        batch.HasIndex(x => new { x.AgentId, x.ReceivedAt, x.RowId });

        var logId = modelBuilder.Entity<HostLogMessageId>();
        logId.ToTable("HostLogMessageIds");
        logId.HasKey(x => new { x.AgentId, x.MessageId }).IsClustered();
        logId.HasIndex(x => x.ReceivedAt);

        var observation = modelBuilder.Entity<RuntimeObservation>();
        observation.ToTable("RuntimeObservations");
        observation.HasKey(x => x.RowId).IsClustered();
        observation.Property(x => x.RowId).UseIdentityColumn();
        observation.Property(x => x.OperatingSystem).HasMaxLength(256).IsRequired();
        observation.Property(x => x.Architecture).HasMaxLength(32).IsRequired();
        observation.Property(x => x.RuntimeVersion).HasMaxLength(128).IsRequired();
        observation.HasIndex(x => new { x.AgentId, x.MessageId }).IsUnique();
        observation.HasIndex(x => new { x.AgentId, x.OccurredAt, x.RowId });

        var metric = modelBuilder.Entity<HostMetric>();
        metric.ToTable("HostMetrics");
        metric.HasKey(x => x.RowId).IsClustered();
        metric.Property(x => x.RowId).UseIdentityColumn();
        metric.Property(x => x.Source).HasMaxLength(128).IsRequired();
        metric.Property(x => x.Dimension).HasMaxLength(256);
        metric.HasIndex(x => new { x.AgentId, x.MessageId }).IsUnique();
        metric.HasIndex(x => new { x.AgentId, x.OccurredAt, x.RowId });
        metric.HasIndex(x => new { x.AgentId, x.Kind, x.OccurredAt, x.RowId });
        metric.HasIndex(x => x.RowId).HasFilter("[Aggregated] = 0").HasDatabaseName("IX_HostMetrics_Unaggregated");
        metric.HasIndex(x => new { x.OccurredAt, x.RowId }).HasFilter("[Aggregated] = 1").HasDatabaseName("IX_HostMetrics_Retention");

        var rollup = modelBuilder.Entity<HostMetricRollup>();
        rollup.ToTable("HostMetricRollups");
        rollup.HasKey(x => x.RowId).IsClustered();
        rollup.Property(x => x.RowId).UseIdentityColumn();
        rollup.Property(x => x.Source).HasMaxLength(128).IsRequired();
        rollup.Property(x => x.Dimension).HasMaxLength(256);
        rollup.Property(x => x.SourceBytes).HasColumnType("varbinary(256)")
            .HasComputedColumnSql("CONVERT(varbinary(256), [Source])", stored: true);
        rollup.Property(x => x.DimensionBytes).HasColumnType("varbinary(512)")
            .HasComputedColumnSql("CONVERT(varbinary(512), [Dimension])", stored: true);
        rollup.HasIndex(x => new { x.AgentId, x.Kind, x.WindowStart, x.SourceBytes, x.DimensionBytes }).IsUnique().HasFilter(null);
        rollup.HasIndex(x => new { x.WindowStart, x.RowId }).HasDatabaseName("IX_HostMetricRollups_Retention");

        var retention = modelBuilder.Entity<HostMetricRetentionSetting>();
        retention.ToTable("HostMetricRetentionSettings", table =>
        {
            table.HasCheckConstraint("CK_HostMetricRetentionSettings_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_HostMetricRetentionSettings_Slot", "[Slot] = 1");
            table.HasCheckConstraint("CK_HostMetricRetentionSettings_Days", "[RawDays] BETWEEN 1 AND 3650 AND [RollupDays] BETWEEN [RawDays] AND 3650");
        });
        retention.HasKey(x => x.Id).IsClustered(false);
        retention.Property(x => x.Id).ValueGeneratedNever();
        retention.Property(x => x.RowId).UseIdentityColumn();
        retention.HasIndex(x => x.RowId).IsUnique().IsClustered();
        retention.HasIndex(x => x.Slot).IsUnique();
        retention.Property(x => x.Version).IsRowVersion();

        var logSource = modelBuilder.Entity<HostLogSource>();
        logSource.ToTable("HostLogSources");
        logSource.HasKey(x => x.Id).IsClustered(false);
        logSource.Property(x => x.Id).ValueGeneratedNever();
        logSource.Property(x => x.Generation).ValueGeneratedNever();
        logSource.Property(x => x.RowId).UseIdentityColumn();
        logSource.HasIndex(x => x.RowId).IsUnique().IsClustered();
        logSource.HasIndex(x => new { x.DeviceId, x.RetiredAt });
        logSource.Property(x => x.Name).HasMaxLength(256).IsRequired();
        logSource.Property(x => x.Target).HasMaxLength(1024).IsRequired();
        logSource.Property(x => x.Version).IsRowVersion();

        var logSourceAudit = modelBuilder.Entity<HostLogSourceAudit>();
        logSourceAudit.ToTable("HostLogSourceAudits");
        logSourceAudit.HasKey(x => x.RowId).IsClustered();
        logSourceAudit.Property(x => x.RowId).UseIdentityColumn();
        logSourceAudit.Property(x => x.Version).HasColumnType("binary(8)").IsRequired();
        logSourceAudit.HasIndex(x => new { x.DeviceId, x.OccurredAt, x.RowId });
        logSourceAudit.HasIndex(x => new { x.OccurredAt, x.RowId });
        logSourceAudit.HasOne<HostLogSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.NoAction);

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

        var threshold = modelBuilder.Entity<HostMetricThreshold>();
        threshold.ToTable("HostMetricThresholds", table =>
        {
            table.HasCheckConstraint("CK_HostMetricThresholds_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_HostMetricThresholds_Comparison", "[Comparison] IN (1, 2)");
            table.HasCheckConstraint("CK_HostMetricThresholds_Freshness", "[FreshnessSeconds] BETWEEN 30 AND 86400");
            table.HasCheckConstraint("CK_HostMetricThresholds_Value", "[Value] >= 0 AND [Value] <= 1e30");
        });
        threshold.HasKey(x => x.Id).IsClustered(false);
        threshold.Property(x => x.Id).ValueGeneratedNever();
        threshold.Property(x => x.RowId).UseIdentityColumn();
        threshold.HasIndex(x => x.RowId).IsUnique().IsClustered();
        threshold.HasIndex(x => new { x.DeviceId, x.SeriesHash }).IsUnique();
        threshold.Property(x => x.SeriesHash).HasColumnType("binary(32)").IsRequired();
        threshold.Property(x => x.Source).HasMaxLength(128).IsRequired();
        threshold.Property(x => x.Dimension).HasMaxLength(256);
        threshold.Property(x => x.Version).IsRowVersion();

        var thresholdAudit = modelBuilder.Entity<HostMetricThresholdAudit>();
        thresholdAudit.ToTable("HostMetricThresholdAudits");
        thresholdAudit.HasKey(x => x.RowId).IsClustered();
        thresholdAudit.Property(x => x.RowId).UseIdentityColumn();
        thresholdAudit.Property(x => x.Version).HasColumnType("binary(8)").IsRequired();
        thresholdAudit.HasIndex(x => new { x.DeviceId, x.OccurredAt, x.RowId });
        thresholdAudit.HasOne<HostMetricThreshold>().WithMany().HasForeignKey(x => x.ThresholdId).OnDelete(DeleteBehavior.NoAction);

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

public sealed class HostMonitoringDesignTimeFactory : IDesignTimeDbContextFactory<HostMonitoringDbContext>
{
    public HostMonitoringDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<HostMonitoringDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=Vigiloom_Host_Development;Integrated Security=true;Encrypt=false",
                sql => sql.MigrationsHistoryTable(HostMonitoringDbContext.MigrationHistory, "agent"))
            .Options);
}
