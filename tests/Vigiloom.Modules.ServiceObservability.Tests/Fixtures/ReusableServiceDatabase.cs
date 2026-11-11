using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Respawn;
using Respawn.Graph;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.ServiceObservability.Tests.Fixtures;

/// <summary>Exclusive, run-owned database for service cases that do not change schema or rejection counters.</summary>
internal static class ReusableServiceDatabase
{
    private static readonly ReusableSqlTestDatabase Pool = new(InitializeAsync);

    internal static Task ExecuteAsync(Func<SqlTestDatabase, CancellationToken, Task> action, CancellationToken token,
        Func<SqlConnection, Task>? resetOverride = null, Func<SqlTestDatabase, ValueTask>? disposeOverride = null) =>
        Pool.ExecuteAsync(action, token, resetOverride, disposeOverride);

    internal static ValueTask DisposeAsync() => Pool.DisposeAsync();

    private static async Task<Func<SqlConnection, CancellationToken, Task>> InitializeAsync(SqlTestDatabase created, CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<ServiceObservabilityDbContext>().UseSqlServer(created.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "service")).Options;
        await using (var db = new ServiceObservabilityDbContext(options)) await db.Database.MigrateAsync(ct);
        var operations = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(created.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
        await using (var db = new OperationsDbContext(operations)) await db.Database.MigrateAsync(ct);
        await using var initialConnection = new SqlConnection(created.ConnectionString);
        await initialConnection.OpenAsync(ct);
        var seedPolicy = await ReadSeedPolicyAsync(initialConnection, ct);
        var respawner = await Respawner.CreateAsync(initialConnection, new RespawnerOptions
        {
            SchemasToInclude = ["service", "operations"],
            TablesToIgnore = [new Table("service", "__EFMigrationsHistory"), new Table("service", "ReceptionRejections"),
                new Table("operations", "__EFMigrationsHistory"), new Table("operations", "WorkerDiagnosticSlots")],
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
        command.CommandText = "SELECT CONCAT((SELECT * FROM [service].[ReceptionRejections] ORDER BY [Kind] FOR JSON PATH, INCLUDE_NULL_VALUES), N'|', (SELECT * FROM [operations].[WorkerDiagnosticSlots] ORDER BY [Slot] FOR JSON PATH, INCLUDE_NULL_VALUES))";
        return (string)(await command.ExecuteScalarAsync(token) ?? throw new InvalidOperationException("Cannot read policy seed."));
    }
}
