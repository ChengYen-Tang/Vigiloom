using Vigiloom.Migrator.Recovery;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Infrastructure.Scheduling.Persistence;

if (args.Length > 0)
{
    if (args.Length != 2 || args[0] != "recover-admin")
        throw new ArgumentException("Usage: Vigiloom.Migrator [recover-admin <account-name>]");
    await LocalRecoveryCommand.RunAsync(args[1]);
    return;
}

var connectionString = Environment.GetEnvironmentVariable("VIGILOOM_MIGRATION_SQL_CONNECTION");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("Set VIGILOOM_MIGRATION_SQL_CONNECTION to the existing target database.");
var target = new SqlConnectionStringBuilder(connectionString);
if (string.IsNullOrWhiteSpace(target.InitialCatalog) ||
    new[] { "master", "model", "msdb", "tempdb" }.Contains(target.InitialCatalog, StringComparer.OrdinalIgnoreCase))
    throw new InvalidOperationException("Specify a non-system target database in the connection string.");
// Open the target first: migration deployment must never implicitly create a mistaken database.
await using (var connection = new SqlConnection(connectionString))
{
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = "SELECT compatibility_level FROM sys.databases WHERE name = DB_NAME()";
    var compatibilityLevel = Convert.ToInt32(await command.ExecuteScalarAsync());
    if (compatibilityLevel < SqlServerRegistration.CompatibilityLevel)
        throw new InvalidOperationException($"Database compatibility level {compatibilityLevel} is below the required {SqlServerRegistration.CompatibilityLevel}.");
}
var services = new ServiceCollection();
services.AddModuleSqlServer<IdentityAccessDbContext>(connectionString, "identity");
services.AddModuleSqlServer<ResourcesDbContext>(connectionString, "resources");
services.AddModuleSqlServer<NetworkDbContext>(connectionString, "network");
services.AddModuleSqlServer<SchedulingDbContext>(connectionString, "scheduling");
using var provider = services.BuildServiceProvider();
await ApplyAsync(provider.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>());
await ApplyAsync(provider.GetRequiredService<IDbContextFactory<ResourcesDbContext>>());
await ApplyAsync(provider.GetRequiredService<IDbContextFactory<NetworkDbContext>>());
await ApplyAsync(provider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>());
await RoleInitializer.InitializeAsync(provider.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>());
await HangfireSchemaInstaller.InstallAsync(connectionString);
Console.WriteLine("Module and Hangfire migrations and built-in role initialization completed.");

static async Task ApplyAsync<TContext>(IDbContextFactory<TContext> factory) where TContext : DbContext
{
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}
