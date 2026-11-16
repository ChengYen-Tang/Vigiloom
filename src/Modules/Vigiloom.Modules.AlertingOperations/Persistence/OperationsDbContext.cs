using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AlertingOperations.Domain;

namespace Vigiloom.Modules.AlertingOperations.Persistence;

public sealed class OperationsDbContext(DbContextOptions<OperationsDbContext> options) : DbContext(options)
{
    internal DbSet<OperationAuditRecord> AuditEvents => Set<OperationAuditRecord>();
    internal DbSet<WorkerDiagnosticSlot> WorkerDiagnosticSlots => Set<WorkerDiagnosticSlot>();
    internal DbSet<PingAlert> PingAlerts => Set<PingAlert>();
    internal DbSet<PingAlertEvent> PingAlertEvents => Set<PingAlertEvent>();
    internal DbSet<Notification> Notifications => Set<Notification>();
    internal DbSet<ServiceHealthAlert> ServiceHealthAlerts => Set<ServiceHealthAlert>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("operations");
        var audit = modelBuilder.Entity<OperationAuditRecord>();
        audit.ToTable("AuditEvents");
        audit.HasKey(x => x.RowId).IsClustered();
        audit.Property(x => x.RowId).UseIdentityColumn();
        audit.Property(x => x.Details).HasMaxLength(2048).IsRequired();
        audit.HasIndex(x => new { x.OccurredAt, x.RowId });

        var slot = modelBuilder.Entity<WorkerDiagnosticSlot>();
        slot.ToTable("WorkerDiagnosticSlots", table =>
        {
            table.HasCheckConstraint("CK_WorkerDiagnosticSlots_Slot", "[Slot] BETWEEN 1 AND 64");
            table.HasCheckConstraint("CK_WorkerDiagnosticSlots_Failures", "[PingFailures] >= 0 AND [SpeedFailures] >= 0");
        });
        slot.HasKey(x => x.Slot).IsClustered();
        slot.Property(x => x.Slot).ValueGeneratedNever();

        var alert = modelBuilder.Entity<PingAlert>();
        alert.ToTable("PingAlerts");
        alert.HasKey(x => x.Id).IsClustered(false);
        alert.Property(x => x.Id).ValueGeneratedNever();
        alert.Property(x => x.RowId).UseIdentityColumn();
        alert.HasIndex(x => x.RowId).IsUnique().IsClustered();
        alert.Property(x => x.Target).HasMaxLength(253).IsRequired();
        alert.HasIndex(x => new { x.DeviceId, x.Target }).IsUnique().HasFilter("[RecoveredAt] IS NULL");
        alert.HasIndex(x => new { x.DeviceId, x.OpenedAt });

        var alertEvent = modelBuilder.Entity<PingAlertEvent>();
        alertEvent.ToTable("PingAlertEvents");
        alertEvent.HasKey(x => x.RowId).IsClustered();
        alertEvent.Property(x => x.RowId).UseIdentityColumn();
        alertEvent.Property(x => x.MessageId).ValueGeneratedNever();
        alertEvent.Property(x => x.DeviceName).HasMaxLength(256).IsRequired();
        alertEvent.Property(x => x.GroupsJson).IsRequired();
        alertEvent.HasIndex(x => x.MessageId).IsUnique();
        alertEvent.HasIndex(x => new { x.OccurredAt, x.RowId });
        alertEvent.HasIndex(x => new { x.AlertId, x.Generation }).IsUnique();
        alertEvent.HasOne<PingAlert>().WithMany().HasForeignKey(x => x.AlertId).OnDelete(DeleteBehavior.Cascade);

        var notification = modelBuilder.Entity<Notification>();
        notification.ToTable("Notifications");
        notification.HasKey(x => x.Id).IsClustered(false);
        notification.Property(x => x.Id).ValueGeneratedNever();
        notification.Property(x => x.RowId).UseIdentityColumn();
        notification.Property(x => x.MessageId).ValueGeneratedNever();
        notification.HasIndex(x => x.RowId).IsUnique().IsClustered();
        notification.HasIndex(x => x.MessageId).IsUnique();
        notification.HasIndex(x => new { x.OccurredAt, x.RowId });
        notification.Property(x => x.Source).HasMaxLength(64).IsRequired();
        notification.Property(x => x.Title).HasMaxLength(256).IsRequired();
        notification.Property(x => x.Detail).IsRequired();

        var serviceAlert = modelBuilder.Entity<ServiceHealthAlert>();
        serviceAlert.ToTable("ServiceHealthAlerts");
        serviceAlert.HasKey(x => x.Id).IsClustered(false);
        serviceAlert.Property(x => x.Id).ValueGeneratedNever();
        serviceAlert.Property(x => x.RowId).UseIdentityColumn();
        serviceAlert.HasIndex(x => x.RowId).IsUnique().IsClustered();
        serviceAlert.HasIndex(x => x.TargetId).IsUnique().HasFilter("[RecoveredAt] IS NULL");
        serviceAlert.HasIndex(x => new { x.ServiceId, x.OpenedAt });

    }
}
