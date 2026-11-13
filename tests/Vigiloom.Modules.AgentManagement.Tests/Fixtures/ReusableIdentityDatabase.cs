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
    private static readonly ReusableSqlTestDatabase Pool = new((database, token) => InitializeAsync(database, false, token));
    private static readonly ReusableSqlTestDatabase SnapshotPool = new((database, token) => InitializeAsync(database, true, token));

    internal static Task ExecuteAsync(Func<string, CancellationToken, Task> action, CancellationToken token,
        Func<SqlConnection, Task>? resetOverride = null, Func<SqlTestDatabase, ValueTask>? disposeOverride = null) =>
        Pool.ExecuteAsync((database, ct) => action(database.ConnectionString, ct), token, resetOverride, disposeOverride);

    internal static Task ExecuteSnapshotAsync(Func<string, CancellationToken, Task> action, CancellationToken token) =>
        SnapshotPool.ExecuteAsync(async (database, ct) =>
        {
            await using (var connection = new SqlConnection(database.ConnectionString))
            {
                await connection.OpenAsync(ct);
                await CheckSnapshotAsync(connection, true, ct);
            }
            await action(database.ConnectionString, ct);
        }, token);

    internal static async ValueTask DisposeAsync() => await Task.WhenAll(Pool.DisposeAsync().AsTask(), SnapshotPool.DisposeAsync().AsTask());

    private static async Task<Func<SqlConnection, CancellationToken, Task>> InitializeAsync(SqlTestDatabase created, bool snapshotReads, CancellationToken ct)
    {
        var master = new SqlConnectionStringBuilder(created.ConnectionString) { InitialCatalog = "master", Pooling = false };
        await using (var connection = new SqlConnection(master.ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER DATABASE [{created.DatabaseName}] SET READ_COMMITTED_SNAPSHOT {(snapshotReads ? "ON" : "OFF")}";
            await command.ExecuteNonQueryAsync(ct);
        }
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
            await CheckSnapshotAsync(connection, snapshotReads, cancellationToken);
            if (await ReadSeedPolicyAsync(connection, cancellationToken) != seedPolicy)
                throw new InvalidOperationException("A reusable test changed its preserved seed.");
            await respawner.ResetAsync(connection);
        };
    }

    private static async Task CheckSnapshotAsync(SqlConnection connection, bool expected, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID()";
        if (await command.ExecuteScalarAsync(token) is not bool actual || actual != expected)
            throw new InvalidOperationException("A reusable test changed its database read-committed snapshot mode.");
    }

    private static async Task<string> ReadSeedPolicyAsync(SqlConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = "SELECT * FROM [agent].[ReplayPolicies] ORDER BY [RowId] FOR JSON PATH, INCLUDE_NULL_VALUES";
        return (string)(await command.ExecuteScalarAsync(token) ?? throw new InvalidOperationException("Cannot read policy seed."));
    }
}
