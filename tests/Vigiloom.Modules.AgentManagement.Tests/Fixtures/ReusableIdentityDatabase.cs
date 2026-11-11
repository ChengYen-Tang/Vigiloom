using System.Runtime.ExceptionServices;
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
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly List<SqlTestDatabase> PendingCleanup = [];
    private static SqlTestDatabase? database;
    private static Respawner? respawner;
    private static string? seedPolicy;

    internal static async Task ExecuteAsync(Func<string, CancellationToken, Task> action, CancellationToken token,
        Func<SqlConnection, Task>? resetOverride = null, Func<SqlTestDatabase, ValueTask>? disposeOverride = null)
    {
        await Gate.WaitAsync(token);
        try
        {
            if (database is null)
            {
                database = await SqlTestDatabase.CreateAsync(TestDatabaseServer.FromEnvironment(), async (created, ct) =>
                {
                    var resources = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(created.ConnectionString,
                        sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
                    await using (var db = new ResourcesDbContext(resources)) await db.Database.MigrateAsync(ct);
                    var agents = new DbContextOptionsBuilder<AgentManagementDbContext>().UseSqlServer(created.ConnectionString,
                        sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "agent")).Options;
                    await using (var db = new AgentManagementDbContext(agents)) await db.Database.MigrateAsync(ct);
                    await using var connection = new SqlConnection(created.ConnectionString);
                    await connection.OpenAsync(ct);
                    seedPolicy = await ReadSeedPolicyAsync(connection, ct);
                    respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
                    {
                        SchemasToInclude = ["agent", "resources"],
                        TablesToIgnore = [new Table("agent", "__EFMigrationsHistory"), new Table("resources", "__EFMigrationsHistory"), new Table("agent", "ReplayPolicies")],
                        CommandTimeout = 30
                    });
                }, token);
            }

            Exception? failure = null;
            try { await action(database.ConnectionString, token); }
            catch (Exception error) { failure = error; }
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                await using var connection = new SqlConnection(database.ConnectionString);
                await connection.OpenAsync(cleanup.Token);
                if (await ReadSeedPolicyAsync(connection, cleanup.Token) != seedPolicy)
                    throw new InvalidOperationException("A reusable test changed the preserved replay-policy seed.");
                if (resetOverride is null) await respawner!.ResetAsync(connection);
                else await resetOverride(connection);
            }
            catch (Exception cleanupFailure)
            {
                var discarded = database;
                database = null;
                respawner = null;
                seedPolicy = null;
                try { if (disposeOverride is null) await discarded.DisposeAsync(); else await disposeOverride(discarded); }
                catch (Exception disposeFailure)
                {
                    PendingCleanup.Add(discarded);
                    if (failure is not null) throw new AggregateException("Test, database reset and disposal failed.", failure, cleanupFailure, disposeFailure);
                    throw new AggregateException("Database reset and disposal failed.", cleanupFailure, disposeFailure);
                }
                if (failure is not null) throw new AggregateException("Test and database reset failed.", failure, cleanupFailure);
                throw;
            }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally { Gate.Release(); }
    }

    internal static async Task DisposeAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (database is not null) { PendingCleanup.Add(database); database = null; respawner = null; seedPolicy = null; }
            List<Exception> failures = [];
            foreach (var item in PendingCleanup)
                try { await item.DisposeAsync(); } catch (Exception error) { failures.Add(error); }
            if (failures.Count != 0) throw new AggregateException("Some test databases could not be cleaned up.", failures);
            PendingCleanup.Clear();
        }
        finally { Gate.Release(); }
    }

    private static async Task<string> ReadSeedPolicyAsync(SqlConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = "SELECT * FROM [agent].[ReplayPolicies] ORDER BY [RowId] FOR JSON PATH, INCLUDE_NULL_VALUES";
        return (string)(await command.ExecuteScalarAsync(token) ?? throw new InvalidOperationException("Cannot read policy seed."));
    }
}
