using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Persistence;

namespace Vigiloom.Migrator.Network;

internal static class NetworkDatabaseCommand
{
    public static async Task RunAsync(bool copyExisting, CancellationToken token = default)
    {
        var target = Environment.GetEnvironmentVariable("VIGILOOM_NETWORK_SQL_CONNECTION")
            ?? throw new InvalidOperationException("Set VIGILOOM_NETWORK_SQL_CONNECTION to an existing network database.");
        SqlTableDataMigration.ValidateTarget(target);
        var source = copyExisting ? Environment.GetEnvironmentVariable("VIGILOOM_MIGRATION_SQL_CONNECTION")
            ?? throw new InvalidOperationException("Set VIGILOOM_MIGRATION_SQL_CONNECTION to the existing platform database.") : null;
        if (source is not null) SqlTableDataMigration.ValidateConnections(source, target);
        await using (var connection = new SqlConnection(target)) await connection.OpenAsync(token);
        var services = new ServiceCollection();
        services.AddModuleSqlServer<NetworkDbContext>(target, "network");
        services.AddModuleSqlServer<SchedulingDbContext>(target, "scheduling");
        using var provider = services.BuildServiceProvider();
        await using (var db = await provider.GetRequiredService<IDbContextFactory<NetworkDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        await using (var db = await provider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        if (source is not null) await NetworkDataMigration.CopyAsync(source, target, token);
        Console.WriteLine(copyExisting ? "Network data copied successfully." : "Network schema initialized successfully.");
    }
}
