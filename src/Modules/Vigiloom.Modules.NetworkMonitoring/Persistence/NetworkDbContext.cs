using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.NetworkMonitoring.Domain;
using Vigiloom.Modules.NetworkMonitoring.Domain.SpeedTests;

namespace Vigiloom.Modules.NetworkMonitoring.Persistence;

public sealed class NetworkDbContext(DbContextOptions<NetworkDbContext> options) : DbContext(options)
{
    public DbSet<SpeedSetting> SpeedSettings => Set<SpeedSetting>();
    public DbSet<SpeedRun> SpeedRuns => Set<SpeedRun>();
    public DbSet<PingSample> PingSamples => Set<PingSample>();
    public DbSet<PingSetting> PingSettings => Set<PingSetting>();
    public DbSet<PingTargetState> PingTargets => Set<PingTargetState>();
    public DbSet<PingStateEvent> PingStateEvents => Set<PingStateEvent>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("network");
        var speedSettings = modelBuilder.Entity<SpeedSetting>();
        speedSettings.ToTable("SpeedSettings", t => t.HasCheckConstraint("CK_SpeedSettings_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'"));
        speedSettings.HasKey(x => x.Id).IsClustered(false);
        speedSettings.Property(x => x.Id).ValueGeneratedNever();
        speedSettings.Property(x => x.RowId).UseIdentityColumn();
        speedSettings.HasIndex(x => x.RowId).IsUnique().IsClustered();
        speedSettings.Property(x => x.Name).HasMaxLength(32);
        speedSettings.HasIndex(x => x.Name).IsUnique();
        speedSettings.Property(x => x.Version).IsRowVersion();
        var run = modelBuilder.Entity<SpeedRun>();
        run.ToTable("SpeedRuns", t => t.HasCheckConstraint("CK_SpeedRuns_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'"));
        run.HasKey(x => x.Id).IsClustered(false);
        run.Property(x => x.Id).ValueGeneratedNever();
        run.Property(x => x.RowId).UseIdentityColumn();
        run.HasIndex(x => x.RowId).IsUnique().IsClustered();
        run.Property(x => x.Location).HasMaxLength(32);
        run.Property(x => x.Provider).HasMaxLength(100);
        run.Property(x => x.Server).HasMaxLength(253);
        run.Property(x => x.City).HasMaxLength(100);
        run.Property(x => x.Country).HasMaxLength(100);
        run.Property(x => x.Isp).HasMaxLength(256);
        run.Property(x => x.DiagnosticCode).HasMaxLength(64);
        run.Property(x => x.Version).IsRowVersion();
        run.HasIndex(x => x.Location).IsUnique().HasFilter("[State] < 2");
        run.HasIndex(x => new { x.RequestedBy, x.RequestId }).IsUnique().HasFilter(null);
        run.HasIndex(x => new { x.RequestedAt, x.RowId });
        var sample = modelBuilder.Entity<PingSample>();
        sample.ToTable("PingSamples");
        sample.HasKey(x => x.RowId).IsClustered();
        sample.Property(x => x.RowId).UseIdentityColumn();
        sample.Property(x => x.Location).HasMaxLength(256).IsRequired();
        sample.HasIndex(x => new { x.DeviceId, x.OccurredAt, x.RowId });
        sample.Property(x => x.Target).HasMaxLength(253);
        sample.Property(x => x.ResolvedAddress).HasMaxLength(64);
        sample.HasIndex(x => new { x.DeviceId, x.Target, x.Generation }).IsUnique().HasFilter("[Generation] IS NOT NULL AND [Target] IS NOT NULL");

        var setting = modelBuilder.Entity<PingSetting>();
        setting.ToTable("PingSettings", t => t.HasCheckConstraint("CK_PingSettings_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'"));
        setting.HasKey(x => x.Id).IsClustered(false);
        setting.Property(x => x.Id).ValueGeneratedNever();
        setting.Property(x => x.RowId).UseIdentityColumn();
        setting.HasIndex(x => x.RowId).IsUnique().IsClustered();
        setting.Property(x => x.Name).HasMaxLength(32);
        setting.HasIndex(x => x.Name).IsUnique();
        setting.Property(x => x.Version).IsRowVersion();

        var state = modelBuilder.Entity<PingTargetState>();
        state.ToTable("PingTargets");
        state.HasKey(x => new { x.DeviceId, x.Target }).IsClustered();
        state.Property(x => x.Target).HasMaxLength(253);
        state.Property(x => x.DeviceVersion).HasMaxLength(8).IsRequired();
        state.Property(x => x.Generation).IsConcurrencyToken();
        state.Property(x => x.Location).HasMaxLength(256);
        state.Property(x => x.ResolvedAddress).HasMaxLength(64);
        state.HasIndex(x => new { x.Enabled, x.DueAt });

        var change = modelBuilder.Entity<PingStateEvent>();
        change.ToTable("PingStateEvents");
        change.HasKey(x => x.RowId).IsClustered();
        change.Property(x => x.RowId).UseIdentityColumn();
        change.Property(x => x.Target).HasMaxLength(253);
        change.HasIndex(x => new { x.DeviceId, x.OccurredAt, x.RowId });
        change.HasIndex(x => new { x.DeviceId, x.Target, x.Generation }).IsUnique();
    }
}
