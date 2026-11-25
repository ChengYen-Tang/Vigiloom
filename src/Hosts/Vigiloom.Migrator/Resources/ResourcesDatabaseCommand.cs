using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Migrator.Resources;

internal static class ResourcesDatabaseCommand
{
    public static async Task RunAsync(bool copyExisting, CancellationToken token = default)
    {
        var target = Environment.GetEnvironmentVariable("VIGILOOM_RESOURCES_SQL_CONNECTION")
            ?? throw new InvalidOperationException("Set VIGILOOM_RESOURCES_SQL_CONNECTION to an existing resource database.");
        SqlTableDataMigration.ValidateTarget(target);
        var source = copyExisting ? Environment.GetEnvironmentVariable("VIGILOOM_MIGRATION_SQL_CONNECTION")
            ?? throw new InvalidOperationException("Set VIGILOOM_MIGRATION_SQL_CONNECTION to the existing platform database.") : null;
        if (source is not null) SqlTableDataMigration.ValidateConnections(source, target);
        await using (var connection = new SqlConnection(target)) await connection.OpenAsync(token);
        var options = new DbContextOptionsBuilder<ResourcesDbContext>().UseSqlServer(target,
            sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel).MigrationsHistoryTable("__EFMigrationsHistory", "resources")).Options;
        await using (var db = new ResourcesDbContext(options)) await db.Database.MigrateAsync(token);
        if (source is not null) await ResourcesDataMigration.CopyAsync(source, target, token);
        Console.WriteLine(copyExisting ? "Resource data copied successfully." : "Resource schema initialized successfully.");
    }
}
