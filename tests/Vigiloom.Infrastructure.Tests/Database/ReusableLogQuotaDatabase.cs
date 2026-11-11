using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Respawn;
using Respawn.Graph;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Infrastructure.Tests.Database;

internal static class ReusableLogQuotaDatabase
{
    private static readonly ReusableSqlTestDatabase Pool = new(InitializeAsync);

    internal static Task ExecuteAsync(Func<SqlTestDatabase, CancellationToken, Task> action, CancellationToken token) => Pool.ExecuteAsync(action, token);
    internal static ValueTask DisposeAsync() => Pool.DisposeAsync();

    private static async Task<Func<SqlConnection, CancellationToken, Task>> InitializeAsync(SqlTestDatabase database, CancellationToken token)
    {
        var options = new DbContextOptionsBuilder<LogAdmissionDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "logquota")).Options;
        await using (var db = new LogAdmissionDbContext(options)) await db.Database.MigrateAsync(token);
        await using var initial = new SqlConnection(database.ConnectionString);
        await initial.OpenAsync(token);
        var respawner = await Respawner.CreateAsync(initial, new RespawnerOptions
        {
            SchemasToInclude = ["logquota"],
            TablesToIgnore = [new Table("logquota", "__EFMigrationsHistory")],
            CommandTimeout = 30
        });
        return async (connection, _) => await respawner.ResetAsync(connection);
    }
}
