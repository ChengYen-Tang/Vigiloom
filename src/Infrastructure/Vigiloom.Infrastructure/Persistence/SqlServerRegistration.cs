using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Vigiloom.Infrastructure.Persistence;

/// <summary>Registers per-operation context factories for module-owned SQL Server contexts.</summary>
public static class SqlServerRegistration
{
    public const int CompatibilityLevel = 150;
    public static IServiceCollection AddModuleSqlServer<TContext>(
        this IServiceCollection services,
        string connectionString,
        string? migrationSchema = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return services.AddModuleSqlServer<TContext>(_ => connectionString, migrationSchema);
    }

    public static IServiceCollection AddModuleSqlServer<TContext>(
        this IServiceCollection services,
        Func<IServiceProvider, string> connectionString,
        string? migrationSchema = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(connectionString);
        return services.AddDbContextFactory<TContext>((provider, options) =>
            options.UseSqlServer(connectionString(provider), sql =>
            {
                sql.UseCompatibilityLevel(CompatibilityLevel);
                if (migrationSchema is not null)
                    sql.MigrationsHistoryTable("__EFMigrationsHistory", migrationSchema);
            }));
    }
}
