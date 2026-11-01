using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Vigiloom.Infrastructure.Persistence;

/// <summary>Registers per-operation context factories for module-owned SQL Server contexts.</summary>
public static class SqlServerRegistration
{
    public static IServiceCollection AddModuleSqlServer<TContext>(
        this IServiceCollection services,
        string connectionString)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return services.AddDbContextFactory<TContext>(
            options => options.UseSqlServer(connectionString));
    }
}
