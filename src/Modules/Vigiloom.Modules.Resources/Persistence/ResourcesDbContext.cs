using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.Resources.Domain;

namespace Vigiloom.Modules.Resources.Persistence;

public sealed class ResourcesDbContext(DbContextOptions<ResourcesDbContext> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<DeviceGroup> DeviceGroups => Set<DeviceGroup>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("resources");
        var device = modelBuilder.Entity<Device>();
        device.ToTable("Devices", table => table.HasCheckConstraint("CK_Devices_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'"));
        device.HasKey(x => x.Id).IsClustered(false);
        device.Property(x => x.Id).ValueGeneratedNever();
        device.Property(x => x.RowId).UseIdentityColumn();
        device.HasIndex(x => x.RowId).IsUnique().IsClustered();
        device.Property(x => x.Name).HasMaxLength(256).IsRequired();
        device.Property(x => x.Address).HasMaxLength(253).IsRequired();
        device.Property(x => x.Version).IsRowVersion();

        var group = modelBuilder.Entity<Group>();
        group.ToTable("Groups", table => table.HasCheckConstraint("CK_Groups_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'"));
        group.HasKey(x => x.Id).IsClustered(false);
        group.Property(x => x.Id).ValueGeneratedNever();
        group.Property(x => x.RowId).UseIdentityColumn();
        group.HasIndex(x => x.RowId).IsUnique().IsClustered();
        group.Property(x => x.Name).HasMaxLength(256).IsRequired();
        group.Property(x => x.Version).IsRowVersion();

        var membership = modelBuilder.Entity<DeviceGroup>();
        membership.ToTable("DeviceGroups");
        membership.HasKey(x => new { x.GroupId, x.DeviceId }).IsClustered();
        membership.HasIndex(x => new { x.DeviceId, x.GroupId });
        membership.HasOne<Group>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.NoAction);
        membership.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.NoAction);
    }
}
