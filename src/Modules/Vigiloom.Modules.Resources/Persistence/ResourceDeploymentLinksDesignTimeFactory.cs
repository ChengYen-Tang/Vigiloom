using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Vigiloom.Modules.Resources.Persistence;

public sealed class ResourceDeploymentLinksDesignTimeFactory : IDesignTimeDbContextFactory<ResourceDeploymentLinksDbContext>
{
    public ResourceDeploymentLinksDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ResourceDeploymentLinksDbContext>()
        .UseSqlServer(Environment.GetEnvironmentVariable("VIGILOOM_DESIGN_SQL_CONNECTION") ?? "Server=(localdb)\\MSSQLLocalDB;Database=VigiloomDev;Trusted_Connection=True;TrustServerCertificate=True",
            sql => sql.MigrationsHistoryTable(ResourceDeploymentLinksDbContext.MigrationHistory, "resources"))
        .Options);
}
