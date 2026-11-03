using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AgentManagement.Domain;

namespace Vigiloom.Modules.AgentManagement.Persistence;

public sealed class AgentManagementDbContext(DbContextOptions<AgentManagementDbContext> options) : DbContext(options)
{
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();
    public DbSet<AgentAudit> Audits => Set<AgentAudit>();
    public DbSet<ReceivedBatch> ReceivedBatches => Set<ReceivedBatch>();
    public DbSet<RuntimeObservation> RuntimeObservations => Set<RuntimeObservation>();
    public DbSet<AgentSetting> Settings => Set<AgentSetting>();
    public DbSet<AgentSettingAudit> SettingAudits => Set<AgentSettingAudit>();
    public DbSet<AgentQueueCapacitySetting> QueueCapacitySettings => Set<AgentQueueCapacitySetting>();
    public DbSet<AgentQueueCapacityAudit> QueueCapacityAudits => Set<AgentQueueCapacityAudit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("agent");
        var agent = modelBuilder.Entity<Agent>();
        agent.ToTable("Agents", table =>
        {
            table.HasCheckConstraint("CK_Agents_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_Agents_HeartbeatSafety", "[HeartbeatSafetyIntervalSeconds] BETWEEN 10 AND 300");
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
    }
}
