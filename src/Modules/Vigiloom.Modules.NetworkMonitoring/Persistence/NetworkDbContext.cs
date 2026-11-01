using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.NetworkMonitoring.Domain;

namespace Vigiloom.Modules.NetworkMonitoring.Persistence;

public sealed class NetworkDbContext(DbContextOptions<NetworkDbContext> options) : DbContext(options)
{
    public DbSet<PingSample> PingSamples => Set<PingSample>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("network");
        var sample = modelBuilder.Entity<PingSample>();
        sample.ToTable("PingSamples");
        sample.HasKey(x => x.RowId).IsClustered();
        sample.Property(x => x.RowId).UseIdentityColumn();
        sample.Property(x => x.Location).HasMaxLength(256).IsRequired();
        sample.HasIndex(x => new { x.DeviceId, x.OccurredAt, x.RowId });
    }
}
