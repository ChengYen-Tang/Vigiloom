using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.IdentityAccess.Persistence;

namespace Vigiloom.Migrator.Identity;

internal static class IdentityDatabaseCommand
{
    public static async Task RunAsync(bool copyExisting, CancellationToken token = default)
    {
        var target = Environment.GetEnvironmentVariable("VIGILOOM_IDENTITY_SQL_CONNECTION")
            ?? throw new InvalidOperationException("Set VIGILOOM_IDENTITY_SQL_CONNECTION to an existing Identity database.");
        IdentityDataMigration.ValidateTarget(target);
        var source = copyExisting ? Environment.GetEnvironmentVariable("VIGILOOM_MIGRATION_SQL_CONNECTION")
            ?? throw new InvalidOperationException("Set VIGILOOM_MIGRATION_SQL_CONNECTION to the existing platform database.") : null;
        if (source is not null) IdentityDataMigration.ValidateConnections(source, target);
        // Require an existing database rather than letting EF create one implicitly.
        await using (var connection = new SqlConnection(target)) await connection.OpenAsync(token);
        var services = new ServiceCollection();
        services.AddModuleSqlServer<IdentityAccessDbContext>(target, "identity");
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        if (source is not null) await IdentityDataMigration.CopyAsync(source, target, token);
        await RoleInitializer.InitializeAsync(factory, token);
        Console.WriteLine(copyExisting ? "Identity schema and existing data copied successfully." : "Identity schema and roles initialized successfully.");
    }
}
