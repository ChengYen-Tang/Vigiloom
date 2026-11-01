using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.Resources.Domain;

namespace Vigiloom.Modules.Resources.Persistence;

public sealed class ResourcesDbContext(DbContextOptions<ResourcesDbContext> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<DeviceGroup> DeviceGroups => Set<DeviceGroup>();
    public DbSet<DeviceTarget> DeviceTargets => Set<DeviceTarget>();
    public DbSet<DeviceAudit> DeviceAudits => Set<DeviceAudit>();

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
        device.Property(x => x.DeepMonitoring).HasColumnName("HasAgent");
        device.Property(x => x.IsEnabled).HasDefaultValue(true);
        device.Property(x => x.Version).IsRowVersion();

        var target = modelBuilder.Entity<DeviceTarget>();
        target.ToTable("DeviceTargets");
        target.Property(x => x.Address).HasMaxLength(253).IsRequired();
        target.HasKey(x => new { x.DeviceId, x.Address }).IsClustered();
        target.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.NoAction);

        var audit = modelBuilder.Entity<DeviceAudit>();
        audit.ToTable("DeviceAudits");
        audit.HasKey(x => x.RowId).IsClustered();
        audit.Property(x => x.RowId).UseIdentityColumn();
        audit.Property(x => x.Action).HasMaxLength(32).IsRequired();
        audit.HasIndex(x => new { x.DeviceId, x.OccurredAt, x.RowId });
        audit.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.NoAction);

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
