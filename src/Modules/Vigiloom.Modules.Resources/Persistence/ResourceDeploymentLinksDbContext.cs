using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.Resources.Domain;

namespace Vigiloom.Modules.Resources.Persistence;

/// <summary>Deployment relationships in the independent resource service database.</summary>
public sealed class ResourceDeploymentLinksDbContext(DbContextOptions<ResourceDeploymentLinksDbContext> options) : DbContext(options)
{
    public const string MigrationHistory = "__ResourceDeploymentMigrationsHistory";
    public DbSet<DeploymentLink> Links => Set<DeploymentLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => DeploymentLinksDbContext.ConfigureLinks(modelBuilder);
}
