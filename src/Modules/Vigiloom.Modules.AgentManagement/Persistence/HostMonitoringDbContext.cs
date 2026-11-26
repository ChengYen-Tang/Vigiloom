using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Vigiloom.Modules.AgentManagement.Domain;

namespace Vigiloom.Modules.AgentManagement.Persistence;

/// <summary>Host-owned observations and receipts; Agent identity belongs to Resources.</summary>
public sealed class HostMonitoringDbContext(DbContextOptions<HostMonitoringDbContext> options) : DbContext(options)
{
    public const string MigrationHistory = "__EFMigrationsHistory_HostMonitoring";
    public DbSet<ReceivedBatch> ReceivedBatches => Set<ReceivedBatch>();
    public DbSet<RuntimeObservation> RuntimeObservations => Set<RuntimeObservation>();
    public DbSet<HostMetric> HostMetrics => Set<HostMetric>();
    public DbSet<HostMetricRollup> HostMetricRollups => Set<HostMetricRollup>();
    public DbSet<HostMetricRetentionSetting> HostMetricRetentionSettings => Set<HostMetricRetentionSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("agent");
        var batch = modelBuilder.Entity<ReceivedBatch>();
        batch.ToTable("ReceivedBatches");
        batch.HasKey(x => x.RowId).IsClustered();
        batch.Property(x => x.RowId).UseIdentityColumn();
        batch.Property(x => x.Digest).HasColumnType("binary(32)").IsRequired();
        batch.HasIndex(x => new { x.AgentId, x.BatchId }).IsUnique();
        batch.HasIndex(x => new { x.AgentId, x.ReceivedAt, x.RowId });

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
