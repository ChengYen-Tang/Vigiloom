using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.ServiceObservability.Domain;

namespace Vigiloom.Modules.ServiceObservability.Persistence;

public sealed class ServiceObservabilityDbContext(DbContextOptions<ServiceObservabilityDbContext> options) : DbContext(options)
{
    public DbSet<ServiceSource> Sources => Set<ServiceSource>();
    public DbSet<ServiceCandidate> Candidates => Set<ServiceCandidate>();
    public DbSet<MonitoredService> Services => Set<MonitoredService>();
    public DbSet<ServiceInstance> Instances => Set<ServiceInstance>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("service");

        var source = modelBuilder.Entity<ServiceSource>();
        source.ToTable("Sources");
        source.HasKey(x => x.Id).IsClustered(false);
        source.Property(x => x.Id).ValueGeneratedNever();
        source.Property(x => x.RowId).UseIdentityColumn();
        source.HasIndex(x => x.RowId).IsUnique().IsClustered();
        source.Property(x => x.Name).HasMaxLength(128).IsRequired();
        source.Property(x => x.Verifier).HasColumnType("binary(32)").IsRequired();

        var candidate = modelBuilder.Entity<ServiceCandidate>();
        candidate.ToTable("Candidates");
        candidate.HasKey(x => x.Id).IsClustered(false);
        candidate.Property(x => x.Id).ValueGeneratedNever();
        candidate.Property(x => x.RowId).UseIdentityColumn();
        candidate.HasIndex(x => x.RowId).IsUnique().IsClustered();
        candidate.Property(x => x.ServiceKey).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2").IsRequired();
        candidate.Property(x => x.Environment).HasMaxLength(64).UseCollation("Latin1_General_100_BIN2").IsRequired();
        candidate.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
        candidate.HasIndex(x => new { x.SourceId, x.ServiceKey, x.Environment }).IsUnique();
        candidate.HasIndex(x => x.LastSeen);
        candidate.HasOne<ServiceSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);

        var service = modelBuilder.Entity<MonitoredService>();
        service.ToTable("Services");
        service.HasKey(x => x.Id).IsClustered(false);
        service.Property(x => x.Id).ValueGeneratedNever();
        service.Property(x => x.RowId).UseIdentityColumn();
        service.HasIndex(x => x.RowId).IsUnique().IsClustered();
        service.Property(x => x.ServiceKey).HasMaxLength(128).UseCollation("Latin1_General_100_BIN2").IsRequired();
        service.Property(x => x.Environment).HasMaxLength(64).UseCollation("Latin1_General_100_BIN2").IsRequired();
        service.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
        service.HasIndex(x => new { x.SourceId, x.ServiceKey, x.Environment }).IsUnique();
        service.HasIndex(x => x.CandidateId).IsUnique();
        service.HasOne<ServiceSource>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
        service.HasAlternateKey(x => new { x.Id, x.SourceId });

        var instance = modelBuilder.Entity<ServiceInstance>();
        instance.ToTable("Instances");
        instance.HasKey(x => x.Id).IsClustered(false);
        instance.Property(x => x.Id).ValueGeneratedNever();
        instance.Property(x => x.RowId).UseIdentityColumn();
        instance.HasIndex(x => x.RowId).IsUnique().IsClustered();
        instance.Property(x => x.ExternalInstanceId).HasMaxLength(256).IsRequired();
        instance.Property(x => x.ExternalInstanceIdBytes)
            .HasColumnType("varbinary(512)")
            .HasComputedColumnSql("CONVERT(varbinary(512), [ExternalInstanceId])", stored: true);
        instance.HasIndex(x => new { x.ServiceId, x.SourceScopeId, x.ExternalInstanceIdBytes })
            .IsUnique().HasFilter("[RetiredAt] IS NULL");
        instance.HasIndex(x => new { x.ServiceId, x.SourceScopeId, x.ExternalInstanceIdBytes, x.RetiredAt });
        instance.HasIndex(x => new { x.Id, x.ServiceId }).IsUnique();
        instance.HasOne<MonitoredService>().WithMany()
            .HasForeignKey(x => new { x.ServiceId, x.SourceScopeId })
            .HasPrincipalKey(x => new { x.Id, x.SourceId }).OnDelete(DeleteBehavior.Restrict);
    }
}
