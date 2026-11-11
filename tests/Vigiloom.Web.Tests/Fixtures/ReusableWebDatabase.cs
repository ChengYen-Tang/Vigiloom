using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Respawn;
using Respawn.Graph;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Web.Tests.Fixtures;

/// <summary>Exclusive run-owned database for approved Web cases; each case creates its own host and clients.</summary>
internal static class ReusableWebDatabase
{
    private static readonly ReusableSqlTestDatabase Pool = new(InitializeAsync);
    internal static Task ExecuteAsync(Func<SqlTestDatabase, CancellationToken, Task> action, CancellationToken token) => Pool.ExecuteAsync(action, token);
    internal static ValueTask DisposeAsync() => Pool.DisposeAsync();

    private static async Task<Func<SqlConnection, CancellationToken, Task>> InitializeAsync(SqlTestDatabase database, CancellationToken token)
    {
        using (var host = PingTestSite.CreateFactory(database.ConnectionString))
        {
            var services = host.Services;
            await using (var db = await services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await services.GetRequiredService<IDbContextFactory<NetworkDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await services.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        }
        await using var initial = new SqlConnection(database.ConnectionString);
        await initial.OpenAsync(token);
        var seed = await ReadSeedAsync(initial, token);
        var respawner = await Respawner.CreateAsync(initial, new RespawnerOptions
        {
            SchemasToInclude = ["identity", "resources", "network", "operations", "agent"],
            TablesToIgnore = [new Table("identity", "__EFMigrationsHistory"), new Table("resources", "__EFMigrationsHistory"),
                new Table("network", "__EFMigrationsHistory"), new Table("operations", "__EFMigrationsHistory"),
                new Table("agent", "__EFMigrationsHistory"), new Table("agent", "ReplayPolicies"),
                new Table("operations", "WorkerDiagnosticSlots")],
            CommandTimeout = 30
        });
        return async (connection, ct) =>
        {
            if (await ReadSeedAsync(connection, ct) != seed) throw new InvalidOperationException("A reusable Web test changed preserved seed data.");
            await respawner.ResetAsync(connection);
        };
    }

    private static async Task<string> ReadSeedAsync(SqlConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = "SELECT (SELECT * FROM [agent].[ReplayPolicies] ORDER BY [RowId] FOR JSON PATH, INCLUDE_NULL_VALUES) AS policy, (SELECT * FROM [operations].[WorkerDiagnosticSlots] ORDER BY [Slot] FOR JSON PATH, INCLUDE_NULL_VALUES) AS slots FOR JSON PATH, WITHOUT_ARRAY_WRAPPER";
        return (string)(await command.ExecuteScalarAsync(token) ?? throw new InvalidOperationException("Cannot read Web test seeds."));
    }
}
