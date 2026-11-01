using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Vigiloom.Infrastructure.Persistence;

namespace Vigiloom.Modules.NetworkMonitoring.Persistence;

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<NetworkDbContext>
{
    public NetworkDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("VIGILOOM_DESIGN_SQL_CONNECTION")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=Vigiloom_Development;Integrated Security=true;TrustServerCertificate=true";
        var options = new DbContextOptionsBuilder<NetworkDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel).MigrationsHistoryTable("__EFMigrationsHistory", "network"))
            .Options;
        return new NetworkDbContext(options);
    }
}
