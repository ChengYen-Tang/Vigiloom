using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Vigiloom.Infrastructure.Persistence;

namespace Vigiloom.Modules.Resources.Persistence;

public sealed class DeploymentLinksDesignTimeFactory : IDesignTimeDbContextFactory<DeploymentLinksDbContext>
{
    public DeploymentLinksDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("VIGILOOM_DESIGN_SQL_CONNECTION")
            ?? "Server=(localdb)\\MSSQLLocalDB;Database=Vigiloom_Development;Integrated Security=true;TrustServerCertificate=true";
        return new(new DbContextOptionsBuilder<DeploymentLinksDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable(DeploymentLinksDbContext.MigrationHistory, "resources"))
            .Options);
    }
}
