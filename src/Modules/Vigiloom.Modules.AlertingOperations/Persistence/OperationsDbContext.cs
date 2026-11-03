using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AlertingOperations.Domain;

namespace Vigiloom.Modules.AlertingOperations.Persistence;

public sealed class OperationsDbContext(DbContextOptions<OperationsDbContext> options) : DbContext(options)
{
    internal DbSet<OperationAuditRecord> AuditEvents => Set<OperationAuditRecord>();
    internal DbSet<WorkerDiagnosticSlot> WorkerDiagnosticSlots => Set<WorkerDiagnosticSlot>();

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
    }
}
