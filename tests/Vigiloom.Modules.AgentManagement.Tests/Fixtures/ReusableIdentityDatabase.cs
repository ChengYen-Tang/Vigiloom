using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Respawn;
using Respawn.Graph;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.AgentManagement.Tests.Fixtures;

/// <summary>Exclusive, run-owned database for identity cases that do not change schema or replay policy.</summary>
internal static class ReusableIdentityDatabase
{
    private static readonly ReusableSqlTestDatabase Pool = new(InitializeAsync);

    internal static Task ExecuteAsync(Func<string, CancellationToken, Task> action, CancellationToken token,
        Func<SqlConnection, Task>? resetOverride = null, Func<SqlTestDatabase, ValueTask>? disposeOverride = null) =>
        Pool.ExecuteAsync((database, ct) => action(database.ConnectionString, ct), token, resetOverride, disposeOverride);

    internal static ValueTask DisposeAsync() => Pool.DisposeAsync();

    private static async Task<Func<SqlConnection, CancellationToken, Task>> InitializeAsync(SqlTestDatabase created, CancellationToken ct)
    {
        var resources = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(created.ConnectionString,
            sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
        await using (var db = new ResourcesDbContext(resources)) await db.Database.MigrateAsync(ct);
        var agents = new DbContextOptionsBuilder<AgentManagementDbContext>().UseSqlServer(created.ConnectionString,
            sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "agent")).Options;
        await using (var db = new AgentManagementDbContext(agents)) await db.Database.MigrateAsync(ct);
        await using var initialConnection = new SqlConnection(created.ConnectionString);
        await initialConnection.OpenAsync(ct);
        var seedPolicy = await ReadSeedPolicyAsync(initialConnection, ct);
        var respawner = await Respawner.CreateAsync(initialConnection, new RespawnerOptions
        {
            SchemasToInclude = ["agent", "resources"],
            TablesToIgnore = [new Table("agent", "__EFMigrationsHistory"), new Table("resources", "__EFMigrationsHistory"), new Table("agent", "ReplayPolicies")],
            CommandTimeout = 30
        });
        return async (connection, cancellationToken) =>
        {
            if (await ReadSeedPolicyAsync(connection, cancellationToken) != seedPolicy)
                throw new InvalidOperationException("A reusable test changed its preserved seed.");
            await respawner.ResetAsync(connection);
        };
    }

    private static async Task<string> ReadSeedPolicyAsync(SqlConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = "SELECT * FROM [agent].[ReplayPolicies] ORDER BY [RowId] FOR JSON PATH, INCLUDE_NULL_VALUES";
        return (string)(await command.ExecuteScalarAsync(token) ?? throw new InvalidOperationException("Cannot read policy seed."));
    }
}
