using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Respawn;
using Respawn.Graph;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Fixtures;

/// <summary>Exclusively leases an owned database to security tests that do not change schema.</summary>
internal static class ReusableSecurityDatabase
{
    private static readonly ReusableSqlTestDatabase Pool = new(InitializeAsync);

    internal static Task ExecuteAsync(Func<SqlTestDatabase, CancellationToken, Task> action, CancellationToken token) =>
        Pool.ExecuteAsync(action, token);

    internal static ValueTask DisposeAsync() => Pool.DisposeAsync();

    private static async Task<Func<SqlConnection, CancellationToken, Task>> InitializeAsync(SqlTestDatabase database, CancellationToken token)
    {
        var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.UseCompatibilityLevel(150).MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
        await using (var db = new SecurityAnalysisDbContext(options)) await db.Database.MigrateAsync(token);
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(token);
        var respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            SchemasToInclude = ["security"],
            TablesToIgnore = [new Table("security", "__EFMigrationsHistory")],
            CommandTimeout = 30
        });
        return async (sql, _) => await respawner.ResetAsync(sql);
    }
}
