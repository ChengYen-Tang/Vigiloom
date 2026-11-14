using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Respawn;
using Respawn.Graph;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.AgentManagement.Tests.Fixtures;

/// <summary>Database for package tests that require the complete audit schema.</summary>
internal static class ReusablePackageDatabase
{
    private static readonly ReusableSqlTestDatabase Pool = new(InitializeAsync);

    internal static Task ExecuteAsync(Func<SqlTestDatabase, CancellationToken, Task> action, CancellationToken token) =>
        Pool.ExecuteAsync(action, token);

    internal static ValueTask DisposeAsync() => Pool.DisposeAsync();

    private static async Task<Func<SqlConnection, CancellationToken, Task>> InitializeAsync(SqlTestDatabase database, CancellationToken token)
    {
        var resources = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
        var agents = new DbContextOptionsBuilder<AgentManagementDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "agent")).Options;
        var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
        await using (var db = new ResourcesDbContext(resources)) await db.Database.MigrateAsync(token);
        await using (var db = new AgentManagementDbContext(agents)) await db.Database.MigrateAsync(token);
        await using (var db = new OperationsDbContext(operations)) await db.Database.MigrateAsync(token);
        await using var initial = new SqlConnection(database.ConnectionString);
        await initial.OpenAsync(token);
        var seed = await ReadSeedAsync(initial, token);
        var respawner = await Respawner.CreateAsync(initial, new RespawnerOptions
        {
            SchemasToInclude = ["resources", "agent", "operations"],
            TablesToIgnore = [new Table("resources", "__EFMigrationsHistory"), new Table("agent", "__EFMigrationsHistory"),
                new Table("operations", "__EFMigrationsHistory"), new Table("agent", "ReplayPolicies"),
                new Table("operations", "WorkerDiagnosticSlots")],
            CommandTimeout = 30
        });
        return async (connection, ct) =>
        {
            if (await ReadSeedAsync(connection, ct) != seed)
                throw new InvalidOperationException("A reusable package test changed preserved seed data.");
            await respawner.ResetAsync(connection);
        };
    }

    private static async Task<string> ReadSeedAsync(SqlConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = "SELECT CONCAT((SELECT * FROM [agent].[ReplayPolicies] ORDER BY [RowId] FOR JSON PATH, INCLUDE_NULL_VALUES), N'|', (SELECT * FROM [operations].[WorkerDiagnosticSlots] ORDER BY [Slot] FOR JSON PATH, INCLUDE_NULL_VALUES))";
        return (string)(await command.ExecuteScalarAsync(token) ?? throw new InvalidOperationException("Cannot read package test seeds."));
    }
}
