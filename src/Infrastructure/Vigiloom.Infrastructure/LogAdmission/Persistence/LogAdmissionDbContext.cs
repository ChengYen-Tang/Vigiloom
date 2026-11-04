using Microsoft.EntityFrameworkCore;

namespace Vigiloom.Infrastructure.LogAdmission.Persistence;

/// <summary>Shared durable admission state for log ingestion.</summary>
public sealed class LogAdmissionDbContext(DbContextOptions<LogAdmissionDbContext> options) : DbContext(options)
{
    internal DbSet<LogAdmissionPolicy> Policies => Set<LogAdmissionPolicy>();
    internal DbSet<LogAdmissionBucket> Buckets => Set<LogAdmissionBucket>();
    internal DbSet<TraceAdmissionPolicy> TracePolicies => Set<TraceAdmissionPolicy>();
    internal DbSet<TraceAdmissionBucket> TraceBuckets => Set<TraceAdmissionBucket>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("logquota");
        var policy = modelBuilder.Entity<LogAdmissionPolicy>();
        policy.ToTable("Policies", table =>
        {
            table.HasCheckConstraint("CK_LogQuotaPolicy_Name", "[Name] = 'central'");
            table.HasCheckConstraint("CK_LogQuotaPolicy_Limits", "[SourceRate] > 0 AND [SourceBurst] > 0 AND [GlobalRate] > 0 AND [GlobalBurst] > 0");
        });
        policy.HasKey(x => x.Id).IsClustered(false);
        policy.Property(x => x.Id).ValueGeneratedNever();
        policy.Property(x => x.RowId).UseIdentityColumn();
        policy.HasIndex(x => x.RowId).IsUnique().IsClustered();
        policy.Property(x => x.Name).HasMaxLength(32).IsRequired();
        policy.HasIndex(x => x.Name).IsUnique();
        policy.Property(x => x.Version).IsRowVersion();

        var bucket = modelBuilder.Entity<LogAdmissionBucket>();
        bucket.ToTable("Buckets", table =>
        {
            table.HasCheckConstraint("CK_LogQuotaBucket_Kind", "[Kind] IN (0, 1, 2)");
            table.HasCheckConstraint("CK_LogQuotaBucket_Tokens", "[Microtokens] >= 0");
        });
        bucket.HasKey(x => x.RowId).IsClustered();
        bucket.Property(x => x.RowId).UseIdentityColumn();
        bucket.HasIndex(x => new { x.Kind, x.SourceId }).IsUnique();
        bucket.Property(x => x.Kind).ValueGeneratedNever();
        bucket.Property(x => x.SourceId).ValueGeneratedNever();
        bucket.Property(x => x.PolicyVersion).HasColumnType("varbinary(8)").IsRequired();

        var tracePolicy = modelBuilder.Entity<TraceAdmissionPolicy>();
        tracePolicy.ToTable("TracePolicies", table =>
        {
            table.HasCheckConstraint("CK_TraceQuotaPolicy_Name", "[Name] = 'central'");
            table.HasCheckConstraint("CK_TraceQuotaPolicy_Limits", "[SourceRate] > 0 AND [SourceBurst] > 0 AND [GlobalRate] > 0 AND [GlobalBurst] > 0");
        });
        tracePolicy.HasKey(x => x.Id).IsClustered(false);
        tracePolicy.Property(x => x.Id).ValueGeneratedNever();
        tracePolicy.Property(x => x.RowId).UseIdentityColumn();
        tracePolicy.HasIndex(x => x.RowId).IsUnique().IsClustered();
        tracePolicy.Property(x => x.Name).HasMaxLength(32).IsRequired();
        tracePolicy.HasIndex(x => x.Name).IsUnique();
        tracePolicy.Property(x => x.Version).IsRowVersion();

        var traceBucket = modelBuilder.Entity<TraceAdmissionBucket>();
        traceBucket.ToTable("TraceBuckets", table =>
        {
            table.HasCheckConstraint("CK_TraceQuotaBucket_Tokens", "[Microtokens] >= 0");
            table.HasCheckConstraint("CK_TraceQuotaBucket_Key", "([SourceId] = '00000000-0000-0000-0000-000000000000' AND [ServiceId] = '00000000-0000-0000-0000-000000000000') OR ([SourceId] <> '00000000-0000-0000-0000-000000000000' AND [ServiceId] <> '00000000-0000-0000-0000-000000000000')");
        });
        traceBucket.HasKey(x => x.RowId).IsClustered();
        traceBucket.Property(x => x.RowId).UseIdentityColumn();
        traceBucket.HasIndex(x => new { x.SourceId, x.ServiceId }).IsUnique();
        traceBucket.Property(x => x.SourceId).ValueGeneratedNever();
        traceBucket.Property(x => x.ServiceId).ValueGeneratedNever();
        traceBucket.Property(x => x.PolicyVersion).HasColumnType("varbinary(8)").IsRequired();
    }
}

internal interface IAdmissionPolicy
{
    Guid Id { get; set; }
    string Name { get; set; }
    int SourceRate { get; set; }
    int SourceBurst { get; set; }
    int GlobalRate { get; set; }
    int GlobalBurst { get; set; }
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
    Guid? UpdatedBy { get; set; }
    byte[] Version { get; set; }
}

internal sealed class TraceAdmissionPolicy : IAdmissionPolicy
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public long RowId { get; set; }
    public string Name { get; set; } = "central";
    public int SourceRate { get; set; } = 200;
    public int SourceBurst { get; set; } = 1_000;
    public int GlobalRate { get; set; } = 2_000;
    public int GlobalBurst { get; set; } = 4_000;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public byte[] Version { get; set; } = [];
}

internal sealed class TraceAdmissionBucket
{
    public long RowId { get; set; }
    public Guid SourceId { get; set; }
    public Guid ServiceId { get; set; }
    public long Microtokens { get; set; }
    public DateTimeOffset LastRefill { get; set; }
    public byte[] PolicyVersion { get; set; } = [];
}

internal sealed class LogAdmissionPolicy : IAdmissionPolicy
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public long RowId { get; set; }
    public string Name { get; set; } = "central";
    public int SourceRate { get; set; } = 20;
    public int SourceBurst { get; set; } = 100;
    public int GlobalRate { get; set; } = 1_000;
    public int GlobalBurst { get; set; } = 2_000;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public byte[] Version { get; set; } = [];
}

internal sealed class LogAdmissionBucket
{
    public long RowId { get; set; }
    public byte Kind { get; set; }
    public Guid SourceId { get; set; }
    public long Microtokens { get; set; }
    public DateTimeOffset LastRefill { get; set; }
    public byte[] PolicyVersion { get; set; } = [];
}
