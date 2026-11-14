using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.Resources.Domain;

namespace Vigiloom.Modules.Resources.Persistence;

/// <summary>Resources-owned relationships that require both device and service schemas.</summary>
public sealed class DeploymentLinksDbContext(DbContextOptions<DeploymentLinksDbContext> options) : DbContext(options)
{
    public const string MigrationHistory = "__DeploymentMigrationsHistory";
    public DbSet<DeploymentLink> Links => Set<DeploymentLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("resources");
        var link = modelBuilder.Entity<DeploymentLink>();
        link.ToTable("DeploymentLinks", table =>
        {
            table.HasCheckConstraint("CK_DeploymentLinks_Ids", "[Id] <> '00000000-0000-0000-0000-000000000000' AND [DeviceId] <> '00000000-0000-0000-0000-000000000000' AND [ServiceInstanceId] <> '00000000-0000-0000-0000-000000000000' AND [AttestedBy] <> '00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_DeploymentLinks_Period", "[EndedAt] IS NULL OR [EndedAt] >= [StartedAt]");
        });
        link.HasKey(x => x.Id).IsClustered(false);
        link.Property(x => x.Id).ValueGeneratedNever();
        link.Property(x => x.RowId).UseIdentityColumn();
        link.HasIndex(x => x.RowId).IsUnique().IsClustered();
        link.Property(x => x.Evidence).HasMaxLength(512).IsRequired();
        link.Property(x => x.Version).IsRowVersion();
        link.HasIndex(x => new { x.DeviceId, x.ServiceInstanceId }).IsUnique().HasFilter("[EndedAt] IS NULL");
        link.HasIndex(x => new { x.ServiceInstanceId, x.EndedAt });
    }
}
