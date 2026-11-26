using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AlertingOperations.Domain.Logs;

namespace Vigiloom.Modules.AlertingOperations.Persistence;

/// <summary>Stores host and application logs in the Operations database.</summary>
public sealed class CentralLogsDbContext(DbContextOptions<CentralLogsDbContext> options) : DbContext(options)
{
    public DbSet<HostLog> HostLogs => Set<HostLog>();
    public DbSet<LogContext> LogContexts => Set<LogContext>();
    public DbSet<LogRecord> LogRecords => Set<LogRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("logs");
        var host = modelBuilder.Entity<HostLog>();
        host.ToTable("HostLogs", table => table.HasCheckConstraint("CK_CentralHostLogs_Gap", """
            ([GapReason] IS NULL AND [GapAffectedCount] IS NULL) OR
            ([GapReason] IS NOT NULL AND [GapReason] BETWEEN 1 AND 5 AND
             ([GapAffectedCount] IS NULL OR [GapAffectedCount] > 0) AND [Content] = N'' AND
             [Provider] IS NULL AND [EventId] IS NULL AND [Level] IS NULL AND [User] IS NULL AND [Process] IS NULL)
            """.Replace("\r\n", "\n", StringComparison.Ordinal)));
        host.HasKey(x => x.RowId).IsClustered();
        host.Property(x => x.RowId).UseIdentityColumn();
        host.Property(x => x.SourceName).HasMaxLength(512).IsRequired();
        host.Property(x => x.Content).IsRequired();
        host.Property(x => x.Provider).HasMaxLength(256);
        host.Property(x => x.Level).HasMaxLength(64);
        host.Property(x => x.User).HasMaxLength(256);
        host.Property(x => x.Process).HasMaxLength(256);
        host.HasIndex(x => new { x.AgentId, x.MessageId }).IsUnique();
        host.HasIndex(x => new { x.ReceivedAt, x.RowId });
        host.HasIndex(x => new { x.AgentId, x.ReceivedAt, x.RowId });
        host.HasIndex(x => new { x.AgentId, x.SourceId, x.ReceivedAt, x.RowId });
        host.HasIndex(x => new { x.AgentId, x.SourceId, x.OccurredAt, x.RowId });

        var context = modelBuilder.Entity<LogContext>();
        context.ToTable("LogContexts", table => table.HasCheckConstraint("CK_CentralLogContexts_Payload", "DATALENGTH([ResourcePayload]) <= 65536 AND DATALENGTH([ScopePayload]) <= 65536"));
        context.HasKey(x => x.RowId).IsClustered();
        context.Property(x => x.RowId).UseIdentityColumn();
        context.HasAlternateKey(x => new { x.RowId, x.ServiceId });
        context.Property(x => x.ResourcePayload).IsRequired();
        context.Property(x => x.ScopePayload).IsRequired();
        context.Property(x => x.ResourceSchemaUrl).HasMaxLength(2048).IsRequired();
        context.Property(x => x.ScopeSchemaUrl).HasMaxLength(2048).IsRequired();

        var log = modelBuilder.Entity<LogRecord>();
        log.ToTable("LogRecords", table =>
        {
            table.HasCheckConstraint("CK_CentralLogRecords_Utc", "DATEPART(TZOFFSET, [OccurredAt]) = 0 AND DATEPART(TZOFFSET, [ReceivedAt]) = 0");
            table.HasCheckConstraint("CK_CentralLogRecords_Payload", "DATALENGTH([RecordPayload]) <= 262144");
        });
        log.HasKey(x => x.RowId).IsClustered();
        log.Property(x => x.RowId).UseIdentityColumn();
        log.Property(x => x.TraceId).HasColumnType("binary(16)");
        log.Property(x => x.SpanId).HasColumnType("binary(8)");
        log.Property(x => x.RecordPayload).IsRequired();
        log.HasIndex(x => new { x.ServiceId, x.OccurredAt, x.RowId });
        log.HasIndex(x => new { x.OccurredAt, x.RowId });
        log.HasIndex(x => new { x.ServiceId, x.ReceivedAt });
        log.HasIndex(x => new { x.ServiceId, x.TraceId, x.OccurredAt }).HasFilter("[TraceId] IS NOT NULL");
        log.HasOne(x => x.Context).WithMany().HasForeignKey(x => new { x.ContextId, x.ServiceId })
            .HasPrincipalKey(x => new { x.RowId, x.ServiceId }).OnDelete(DeleteBehavior.Restrict);
    }
}
