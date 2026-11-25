using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Migrator.Operations;

internal static class OperationsDatabaseCommand
{
    public static async Task RunAsync(CancellationToken token = default)
    {
        var target = Environment.GetEnvironmentVariable("VIGILOOM_OPERATIONS_SQL_CONNECTION")
            ?? throw new InvalidOperationException("Set VIGILOOM_OPERATIONS_SQL_CONNECTION to an existing operations database.");
        var database = new SqlConnectionStringBuilder(target).InitialCatalog;
        if (string.IsNullOrWhiteSpace(database) || new[] { "master", "model", "msdb", "tempdb" }.Contains(database, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("Specify an existing non-system operations database.");
        await using (var connection = new SqlConnection(target)) await connection.OpenAsync(token);
        var services = new ServiceCollection();
        services.AddModuleSqlServer<OperationsDbContext>(target, "operations");
        services.AddModuleSqlServer<LogAdmissionDbContext>(target, "logquota");
        services.AddModuleSqlServer<CentralLogsDbContext>(target, "logs", migrationHistory: "__EFMigrationsHistory_CentralLogs");
        using var provider = services.BuildServiceProvider();
        await using (var db = await provider.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        await using (var db = await provider.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        await using (var db = await provider.GetRequiredService<IDbContextFactory<CentralLogsDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        Console.WriteLine("Operations schema initialized successfully.");
    }
}
