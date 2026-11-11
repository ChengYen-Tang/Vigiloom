using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Respawn;
using Respawn.Graph;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Infrastructure.Tests.Database;

internal static class ReusableOutboxDatabase
{
    private static readonly ReusableSqlTestDatabase Pool = new(InitializeAsync);

    internal static Task ExecuteAsync(Func<SqlTestDatabase, CancellationToken, Task> action, CancellationToken token) => Pool.ExecuteAsync(action, token);
    internal static ValueTask DisposeAsync() => Pool.DisposeAsync();

    private static async Task<Func<SqlConnection, CancellationToken, Task>> InitializeAsync(SqlTestDatabase database, CancellationToken token)
    {
        var options = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
        await using (var db = new SchedulingDbContext(options)) await db.Database.MigrateAsync(token);
        await using var initial = new SqlConnection(database.ConnectionString);
        await initial.OpenAsync(token);
        await using (var command = initial.CreateCommand())
        {
            command.CommandText = "CREATE TABLE scheduling.TestEffects (MessageId uniqueidentifier NOT NULL PRIMARY KEY)";
            await command.ExecuteNonQueryAsync(token);
        }
        var respawner = await Respawner.CreateAsync(initial, new RespawnerOptions
        {
            SchemasToInclude = ["scheduling"],
            TablesToIgnore = [new Table("scheduling", "__EFMigrationsHistory")],
            CommandTimeout = 30
        });
        return async (connection, _) => await respawner.ResetAsync(connection);
    }
}
