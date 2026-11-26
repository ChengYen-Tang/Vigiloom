using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Migrator.Observability;

internal static class ObservabilityDatabaseCommand
{
    public static async Task RunAsync(bool copyExisting, CancellationToken token = default)
    {
        var target = Environment.GetEnvironmentVariable("VIGILOOM_OBSERVABILITY_SQL_CONNECTION")
            ?? throw new InvalidOperationException("Set VIGILOOM_OBSERVABILITY_SQL_CONNECTION to an existing observability database.");
        SqlTableDataMigration.ValidateTarget(target);
        var source = copyExisting ? Environment.GetEnvironmentVariable("VIGILOOM_MIGRATION_SQL_CONNECTION")
            ?? throw new InvalidOperationException("Set VIGILOOM_MIGRATION_SQL_CONNECTION to the existing platform database.") : null;
        if (source is not null) SqlTableDataMigration.ValidateConnections(source, target);
        await using (var connection = new SqlConnection(target)) await connection.OpenAsync(token);
        var services = new ServiceCollection();
        services.AddModuleSqlServer<ServiceObservabilityDbContext>(target, "service");
        services.AddModuleSqlServer<SchedulingDbContext>(target, "scheduling");
        using var provider = services.BuildServiceProvider();
        await using (var db = await provider.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        await using (var db = await provider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        if (source is not null) await ObservabilityDataMigration.CopyAsync(source, target, token);
        Console.WriteLine(copyExisting ? "Observability data copied successfully." : "Observability schema initialized successfully.");
    }
}
