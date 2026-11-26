using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Migrator.HostMonitoring;

internal static class HostMonitoringDatabaseCommand
{
    public static async Task RunAsync(bool copyExisting, CancellationToken token = default)
    {
        var target = Environment.GetEnvironmentVariable("VIGILOOM_HOST_SQL_CONNECTION")
            ?? throw new InvalidOperationException("Set VIGILOOM_HOST_SQL_CONNECTION to an existing host monitoring database.");
        var source = copyExisting ? Environment.GetEnvironmentVariable("VIGILOOM_MIGRATION_SQL_CONNECTION")
            ?? throw new InvalidOperationException("Set VIGILOOM_MIGRATION_SQL_CONNECTION to the existing platform database.") : null;
        await RunAsync(target, source, token);
    }

    internal static async Task RunAsync(string target, string? source, CancellationToken token)
    {
        SqlTableDataMigration.ValidateTarget(target);
        if (source is not null) SqlTableDataMigration.ValidateConnections(source, target);
        await using (var connection = new SqlConnection(target)) await connection.OpenAsync(token);
        var options = new DbContextOptionsBuilder<HostMonitoringDbContext>().UseSqlServer(target,
            sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable(HostMonitoringDbContext.MigrationHistory, "agent")).Options;
        await using (var db = new HostMonitoringDbContext(options)) await db.Database.MigrateAsync(token);
        if (source is not null)
            await SqlTableDataMigration.CopyAsync(source, target, "agent",
                ["ReceivedBatches", "RuntimeObservations", "HostMetrics", "HostMetricRollups", "HostMetricRetentionSettings"], token);
        Console.WriteLine(source is not null ? "Host observations copied successfully." : "Host observation schema initialized successfully.");
    }
}
