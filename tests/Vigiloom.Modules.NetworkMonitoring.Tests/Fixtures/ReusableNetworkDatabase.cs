using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Respawn;
using Respawn.Graph;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.NetworkMonitoring.Tests.Fixtures;

/// <summary>Exclusive run-owned database for network tests that leave schema and migration seeds unchanged.</summary>
internal static class ReusableNetworkDatabase
{
    private static readonly ReusableSqlTestDatabase Pool = new(InitializeAsync);

    internal static Task ExecuteAsync(Func<SqlTestDatabase, CancellationToken, Task> action, CancellationToken token) =>
        Pool.ExecuteAsync(action, token);

    internal static ValueTask DisposeAsync() => Pool.DisposeAsync();

    private static async Task<Func<SqlConnection, CancellationToken, Task>> InitializeAsync(SqlTestDatabase database, CancellationToken token)
    {
        var resources = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
        var network = new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "network")).Options;
        var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
        await using (var db = new ResourcesDbContext(resources)) await db.Database.MigrateAsync(token);
        await using (var db = new NetworkDbContext(network)) await db.Database.MigrateAsync(token);
        await using (var db = new OperationsDbContext(operations)) await db.Database.MigrateAsync(token);
        await using var initial = new SqlConnection(database.ConnectionString);
        await initial.OpenAsync(token);
        var seed = await ReadSeedAsync(initial, token);
        var respawner = await Respawner.CreateAsync(initial, new RespawnerOptions
        {
            SchemasToInclude = ["resources", "network", "operations"],
            TablesToIgnore = [new Table("resources", "__EFMigrationsHistory"), new Table("network", "__EFMigrationsHistory"),
                new Table("operations", "__EFMigrationsHistory"), new Table("operations", "WorkerDiagnosticSlots")],
            CommandTimeout = 30
        });
        return async (connection, ct) =>
        {
            if (await ReadSeedAsync(connection, ct) != seed)
                throw new InvalidOperationException("A reusable network test changed its preserved seed.");
            await respawner.ResetAsync(connection);
        };
    }

    private static async Task<string> ReadSeedAsync(SqlConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = "SELECT (SELECT * FROM [operations].[WorkerDiagnosticSlots] ORDER BY [Slot] FOR JSON PATH, INCLUDE_NULL_VALUES)";
        return (string)(await command.ExecuteScalarAsync(token) ?? throw new InvalidOperationException("Cannot read network test seeds."));
    }
}
