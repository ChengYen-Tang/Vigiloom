using Microsoft.EntityFrameworkCore;

namespace Vigiloom.Infrastructure.Scheduling.Persistence;

public sealed class SchedulingDbContext(DbContextOptions<SchedulingDbContext> options) : DbContext(options)
{
    internal DbSet<OutboxRecord> Outbox => Set<OutboxRecord>();
    internal DbSet<DashboardAuditRecord> DashboardAudit => Set<DashboardAuditRecord>();
    internal DbSet<ScheduleSetting> ScheduleSettings => Set<ScheduleSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("scheduling");
        var outbox = modelBuilder.Entity<OutboxRecord>();
        outbox.ToTable("Outbox");
        outbox.HasKey(record => record.RowId).IsClustered();
        outbox.Property(record => record.RowId).UseIdentityColumn();
        outbox.Property(record => record.MessageId).ValueGeneratedNever();
        outbox.HasIndex(record => record.MessageId).IsUnique();
        outbox.Property(record => record.MessageType).HasMaxLength(128).IsRequired();
        outbox.Property(record => record.Payload).HasMaxLength(16384).IsRequired();
        outbox.Property(record => record.LastFailureCode).HasMaxLength(128);
        outbox.HasIndex(record => new { record.State, record.AvailableAt, record.RowId });

        var audit = modelBuilder.Entity<DashboardAuditRecord>();
        audit.ToTable("DashboardAudit");
        audit.HasKey(record => record.RowId).IsClustered();
        audit.Property(record => record.RowId).UseIdentityColumn();
        audit.Property(record => record.Action).HasMaxLength(128).IsRequired();
        audit.Property(record => record.JobId).HasMaxLength(128);
        audit.Property(record => record.Result).HasMaxLength(32).IsRequired();
        audit.HasIndex(record => new { record.OccurredAt, record.RowId });

        var setting = modelBuilder.Entity<ScheduleSetting>();
        setting.ToTable("ScheduleSettings");
        setting.HasKey(record => record.Id).IsClustered(false);
        setting.Property(record => record.Id).ValueGeneratedNever();
        setting.Property(record => record.RowId).UseIdentityColumn();
        setting.HasIndex(record => record.RowId).IsUnique().IsClustered();
        setting.Property(record => record.Name).HasMaxLength(128).IsRequired();
        setting.HasIndex(record => record.Name).IsUnique();
        setting.Property(record => record.Version).IsRowVersion();
    }
}

internal sealed class ScheduleSetting
{
    public Guid Id { get; set; }
    public long RowId { get; set; }
    public string Name { get; set; } = "";
    public int IntervalMinutes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public byte[] Version { get; set; } = [];
}

internal sealed class OutboxRecord
{
    public long RowId { get; set; }
    public Guid MessageId { get; set; }
    public string MessageType { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset AvailableAt { get; set; }
    public int State { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? LastFailureCode { get; set; }
}

internal sealed class DashboardAuditRecord
{
    public long RowId { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string Action { get; set; } = "";
    public string? JobId { get; set; }
    public string Result { get; set; } = "";
}
