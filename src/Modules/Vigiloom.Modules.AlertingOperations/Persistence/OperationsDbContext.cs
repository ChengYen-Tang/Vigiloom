using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AlertingOperations.Domain;

namespace Vigiloom.Modules.AlertingOperations.Persistence;

public sealed class OperationsDbContext(DbContextOptions<OperationsDbContext> options) : DbContext(options)
{
    internal DbSet<OperationAuditRecord> AuditEvents => Set<OperationAuditRecord>();
    internal DbSet<AuditRetentionSetting> AuditRetentionSettings => Set<AuditRetentionSetting>();
    internal DbSet<WorkerDiagnosticSlot> WorkerDiagnosticSlots => Set<WorkerDiagnosticSlot>();
    internal DbSet<PingAlert> PingAlerts => Set<PingAlert>();
    internal DbSet<PingAlertEvent> PingAlertEvents => Set<PingAlertEvent>();
    internal DbSet<Notification> Notifications => Set<Notification>();
    internal DbSet<ServiceHealthAlert> ServiceHealthAlerts => Set<ServiceHealthAlert>();
    internal DbSet<LineSettings> LineSettings => Set<LineSettings>();
    internal DbSet<LineGroup> LineGroups => Set<LineGroup>();
    internal DbSet<LineDelivery> LineDeliveries => Set<LineDelivery>();
    internal DbSet<TimelineRecord> TimelineRecords => Set<TimelineRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("operations");
        var timeline = modelBuilder.Entity<TimelineRecord>();
        timeline.ToTable("TimelineRecords");
        timeline.HasKey(x => x.RowId).IsClustered();
        timeline.Property(x => x.RowId).UseIdentityColumn();
        timeline.Property(x => x.MessageId).ValueGeneratedNever();
        timeline.Property(x => x.Source).HasMaxLength(32).IsRequired();
        timeline.Property(x => x.ResourceKind).HasMaxLength(16).IsRequired();
        timeline.Property(x => x.Summary).IsRequired();
        timeline.Property(x => x.Evidence).IsRequired();
        timeline.Property(x => x.Link).IsRequired();
        timeline.HasIndex(x => x.MessageId).IsUnique();
        timeline.HasIndex(x => new { x.Time, x.RowId });
        timeline.HasIndex(x => new { x.ResourceId, x.Time, x.RowId });
        var audit = modelBuilder.Entity<OperationAuditRecord>();
        audit.ToTable("AuditEvents");
        audit.HasKey(x => x.RowId).IsClustered();
        audit.Property(x => x.RowId).UseIdentityColumn();
        audit.Property(x => x.Details).HasMaxLength(2048).IsRequired();
        audit.HasIndex(x => new { x.OccurredAt, x.RowId });

        var retention = modelBuilder.Entity<AuditRetentionSetting>();
        retention.ToTable("AuditRetentionSettings", table =>
        {
            table.HasCheckConstraint("CK_AuditRetentionSettings_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_AuditRetentionSettings_Slot", "[Slot] = 1");
            table.HasCheckConstraint("CK_AuditRetentionSettings_Days", "[Days] BETWEEN 1 AND 3650");
        });
        retention.HasKey(x => x.Id).IsClustered(false);
        retention.Property(x => x.Id).ValueGeneratedNever();
        retention.Property(x => x.RowId).UseIdentityColumn();
        retention.HasIndex(x => x.RowId).IsUnique().IsClustered();
        retention.HasIndex(x => x.Slot).IsUnique();
        retention.Property(x => x.Days).HasDefaultValue(365);
        retention.Property(x => x.Version).IsRowVersion();

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

        var pingCursor = modelBuilder.Entity<PingTransitionCursor>();
        pingCursor.ToTable("PingTransitionCursors");
        pingCursor.HasKey(x => x.RowId).IsClustered();
        pingCursor.Property(x => x.RowId).UseIdentityColumn();
        pingCursor.Property(x => x.Target).HasMaxLength(253).IsRequired();
        pingCursor.HasIndex(x => new { x.DeviceId, x.Target }).IsUnique();

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

        var lineSettings = modelBuilder.Entity<LineSettings>();
        lineSettings.ToTable("LineSettings", table => table.HasCheckConstraint("CK_LineSettings_Slot", "[Slot] = 1"));
        lineSettings.HasKey(x => x.Id).IsClustered(false);
        lineSettings.Property(x => x.Id).ValueGeneratedNever();
        lineSettings.Property(x => x.RowId).UseIdentityColumn();
        lineSettings.HasIndex(x => x.RowId).IsUnique().IsClustered();
        lineSettings.HasIndex(x => x.Slot).IsUnique();
        lineSettings.Property(x => x.Slot).ValueGeneratedNever();
        lineSettings.Property(x => x.AggregationSeconds).HasDefaultValue(10).HasSentinel(-1);

        var lineGroup = modelBuilder.Entity<LineGroup>();
        lineGroup.ToTable("LineGroups");
        lineGroup.HasKey(x => x.Id).IsClustered(false);
        lineGroup.Property(x => x.Id).ValueGeneratedNever();
        lineGroup.Property(x => x.RowId).UseIdentityColumn();
        lineGroup.HasIndex(x => x.RowId).IsUnique().IsClustered();
        lineGroup.Property(x => x.ExternalId).HasMaxLength(128).IsRequired();
        lineGroup.HasIndex(x => x.ExternalId).IsUnique();
        lineGroup.Property(x => x.Name).HasMaxLength(256);

        var lineDelivery = modelBuilder.Entity<LineDelivery>();
        lineDelivery.ToTable("LineDeliveries");
        lineDelivery.HasKey(x => x.Id).IsClustered(false);
        lineDelivery.Property(x => x.Id).ValueGeneratedNever();
        lineDelivery.Property(x => x.RowId).UseIdentityColumn();
        lineDelivery.HasIndex(x => x.RowId).IsUnique().IsClustered();
        lineDelivery.HasIndex(x => new { x.NotificationId, x.GroupId }).IsUnique().HasFilter("[NotificationId] IS NOT NULL");
        lineDelivery.Property(x => x.Recipient).HasMaxLength(128).IsRequired();
        lineDelivery.Property(x => x.Text).HasMaxLength(5000).IsRequired();
        lineDelivery.Property(x => x.FailureCode).HasMaxLength(64);

    }
}
