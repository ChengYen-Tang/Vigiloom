using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Migrator.Operations;

internal static class OperationsDatabaseCommand
{
    public static async Task RunAsync(bool copyExisting, CancellationToken token = default)
    {
        var target = Environment.GetEnvironmentVariable("VIGILOOM_OPERATIONS_SQL_CONNECTION")
            ?? throw new InvalidOperationException("Set VIGILOOM_OPERATIONS_SQL_CONNECTION to an existing operations database.");
        SqlTableDataMigration.ValidateTarget(target);
        var source = copyExisting ? Environment.GetEnvironmentVariable("VIGILOOM_MIGRATION_SQL_CONNECTION")
            ?? throw new InvalidOperationException("Set VIGILOOM_MIGRATION_SQL_CONNECTION to the existing platform database.") : null;
        if (source is not null) SqlTableDataMigration.ValidateConnections(source, target);
        await using (var connection = new SqlConnection(target)) await connection.OpenAsync(token);
        var services = new ServiceCollection();
        services.AddModuleSqlServer<OperationsDbContext>(target, "operations");
        services.AddModuleSqlServer<LogAdmissionDbContext>(target, "logquota");
        services.AddModuleSqlServer<CentralLogsDbContext>(target, "logs", migrationHistory: "__EFMigrationsHistory_CentralLogs");
        services.AddModuleSqlServer<SchedulingDbContext>(target, "scheduling");
        using var provider = services.BuildServiceProvider();
        await using (var db = await provider.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        await using (var db = await provider.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        await using (var db = await provider.GetRequiredService<IDbContextFactory<CentralLogsDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        await using (var db = await provider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        await HangfireSchemaInstaller.InstallAsync(target, token);
        if (source is not null) await OperationsDataMigration.CopyAsync(source, target, token);
        Console.WriteLine(copyExisting ? "Operations data copied successfully." : "Operations schema initialized successfully.");
    }
}
