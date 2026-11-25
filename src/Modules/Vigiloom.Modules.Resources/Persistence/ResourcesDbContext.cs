using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.Resources.Domain;

namespace Vigiloom.Modules.Resources.Persistence;

public sealed class ResourcesDbContext(DbContextOptions<ResourcesDbContext> options) : DbContext(options)
{
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<DeviceGroup> DeviceGroups => Set<DeviceGroup>();
    public DbSet<ServiceGroup> ServiceGroups => Set<ServiceGroup>();
    public DbSet<DeviceTarget> DeviceTargets => Set<DeviceTarget>();
    public DbSet<DeviceAudit> DeviceAudits => Set<DeviceAudit>();
    public DbSet<ApplicationService> ApplicationServices => Set<ApplicationService>();
    public DbSet<ApplicationCandidate> ApplicationCandidates => Set<ApplicationCandidate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("resources");
        var candidate = modelBuilder.Entity<ApplicationCandidate>();
        candidate.ToTable("ApplicationCandidates");
        candidate.HasKey(x => x.Id).IsClustered(false);
        candidate.Property(x => x.Id).ValueGeneratedNever();
        candidate.Property(x => x.RowId).UseIdentityColumn();
        candidate.HasIndex(x => x.RowId).IsUnique().IsClustered();
        candidate.Property(x => x.ServiceKey).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2").IsRequired();
        candidate.Property(x => x.ServiceNamespace).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2").HasDefaultValue("").IsRequired();
        candidate.Property(x => x.ServiceNamespaceBytes).HasColumnType("varbinary(256)").HasComputedColumnSql("CONVERT(varbinary(256), [ServiceNamespace])", stored: true);
        candidate.Property(x => x.Environment).HasMaxLength(64).UseCollation("Latin1_General_100_BIN2").IsRequired();
        candidate.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
        candidate.HasIndex(x => new { x.SourceId, x.ServiceNamespaceBytes, x.ServiceKey, x.Environment }).IsUnique();
        candidate.HasIndex(x => x.LastSeen);

        var service = modelBuilder.Entity<ApplicationService>();
        service.ToTable("ApplicationServices", table =>
        {
            table.HasCheckConstraint("CK_ApplicationServices_PublicPeriod", "([IsPublic] = 0 AND [PublicSince] IS NULL) OR ([IsPublic] = 1 AND [PublicSince] IS NOT NULL)");
            table.HasCheckConstraint("CK_ApplicationServices_Archived", "[ArchivedAt] IS NULL OR ([IsPublic] = 0 AND [RevokedAt] IS NOT NULL)");
        });
        service.HasKey(x => x.Id).IsClustered(false);
        service.Property(x => x.Id).ValueGeneratedNever();
        service.Property(x => x.RowId).UseIdentityColumn();
        service.HasIndex(x => x.RowId).IsUnique().IsClustered();
        service.Property(x => x.Version).IsRowVersion();
        service.Property(x => x.ServiceKey).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2").IsRequired();
        service.Property(x => x.ServiceNamespace).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2").HasDefaultValue("").IsRequired();
        service.Property(x => x.ServiceNamespaceBytes).HasColumnType("varbinary(256)").HasComputedColumnSql("CONVERT(varbinary(256), [ServiceNamespace])", stored: true);
        service.Property(x => x.Environment).HasMaxLength(64).UseCollation("Latin1_General_100_BIN2").IsRequired();
        service.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
        service.Property(x => x.PublicDisplayName).HasMaxLength(256);
        service.Property(x => x.PublicPolicyVersion).HasMaxLength(32);
        service.HasIndex(x => new { x.SourceId, x.ServiceNamespaceBytes, x.ServiceKey, x.Environment }).IsUnique();
        service.HasIndex(x => x.CandidateId).IsUnique().HasFilter("[CandidateId] IS NOT NULL");
        service.HasIndex(x => new { x.IsPublic, x.PublicDisplayName, x.RowId });
        var device = modelBuilder.Entity<Device>();
        device.ToTable("Devices", table =>
        {
            table.HasCheckConstraint("CK_Devices_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_Devices_PublicPeriod", "([IsPublic] = 0 AND [PublicSince] IS NULL) OR ([IsPublic] = 1 AND [PublicSince] IS NOT NULL)");
        });
        device.HasKey(x => x.Id).IsClustered(false);
        device.Property(x => x.Id).ValueGeneratedNever();
        device.Property(x => x.RowId).UseIdentityColumn();
        device.HasIndex(x => x.RowId).IsUnique().IsClustered();
        device.Property(x => x.Name).HasMaxLength(256).IsRequired();
        device.Property(x => x.Address).HasMaxLength(253).IsRequired();
        device.Property(x => x.PublicDisplayName).HasMaxLength(256);
        device.Property(x => x.PublicPolicyVersion).HasMaxLength(32);
        device.HasIndex(x => x.PublicTemplateId).HasFilter("[PublicTemplateId] IS NOT NULL");
        device.HasIndex(x => new { x.IsPublic, x.PublicDisplayName, x.RowId });
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
        audit.HasIndex(x => new { x.OccurredAt, x.RowId });
        audit.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.NoAction);

        var group = modelBuilder.Entity<Group>();
        group.ToTable("Groups", table => table.HasCheckConstraint("CK_Groups_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'"));
        group.HasKey(x => x.Id).IsClustered(false);
        group.Property(x => x.Id).ValueGeneratedNever();
        group.Property(x => x.RowId).UseIdentityColumn();
        group.HasIndex(x => x.RowId).IsUnique().IsClustered();
        group.Property(x => x.Name).HasMaxLength(256).IsRequired();
        group.HasIndex(x => x.Name).IsUnique();
        group.Property(x => x.Description).HasMaxLength(1024);
        group.Property(x => x.PublicDisplayName).HasMaxLength(256);
        group.Property(x => x.PublicPolicyVersion).HasMaxLength(32);
        group.HasIndex(x => new { x.IsPublic, x.PublicDisplayName, x.RowId });
        group.Property(x => x.Version).IsRowVersion();

        var membership = modelBuilder.Entity<DeviceGroup>();
        membership.ToTable("DeviceGroups");
        membership.HasKey(x => new { x.GroupId, x.DeviceId }).IsClustered();
        membership.HasIndex(x => new { x.DeviceId, x.GroupId });
        membership.HasOne<Group>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.NoAction);
        membership.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.NoAction);

        var serviceMembership = modelBuilder.Entity<ServiceGroup>();
        serviceMembership.ToTable("ServiceGroups");
        serviceMembership.HasKey(x => new { x.GroupId, x.ServiceId }).IsClustered();
        serviceMembership.HasIndex(x => new { x.ServiceId, x.GroupId });
        serviceMembership.HasOne<Group>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.NoAction);
    }
}
