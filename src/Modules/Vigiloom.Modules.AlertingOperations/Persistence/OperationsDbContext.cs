using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AlertingOperations.Domain;

namespace Vigiloom.Modules.AlertingOperations.Persistence;

public sealed class OperationsDbContext(DbContextOptions<OperationsDbContext> options) : DbContext(options)
{
    internal DbSet<OperationAuditRecord> AuditEvents => Set<OperationAuditRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("operations");
        var audit = modelBuilder.Entity<OperationAuditRecord>();
        audit.ToTable("AuditEvents");
        audit.HasKey(x => x.RowId).IsClustered();
        audit.Property(x => x.RowId).UseIdentityColumn();
        audit.Property(x => x.Details).HasMaxLength(2048).IsRequired();
        audit.HasIndex(x => new { x.OccurredAt, x.RowId });
    }
}
