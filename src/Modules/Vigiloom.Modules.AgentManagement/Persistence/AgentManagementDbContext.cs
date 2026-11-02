using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AgentManagement.Domain;

namespace Vigiloom.Modules.AgentManagement.Persistence;

public sealed class AgentManagementDbContext(DbContextOptions<AgentManagementDbContext> options) : DbContext(options)
{
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<Enrollment> Enrollments => Set<Enrollment>();
    public DbSet<AgentAudit> Audits => Set<AgentAudit>();
    public DbSet<ReceivedBatch> ReceivedBatches => Set<ReceivedBatch>();
    public DbSet<RuntimeObservation> RuntimeObservations => Set<RuntimeObservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("agent");
        var agent = modelBuilder.Entity<Agent>();
        agent.ToTable("Agents", table => table.HasCheckConstraint("CK_Agents_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'"));
        agent.HasKey(x => x.Id).IsClustered(false);
        agent.Property(x => x.Id).ValueGeneratedNever();
        agent.Property(x => x.RowId).UseIdentityColumn();
        agent.HasIndex(x => x.RowId).IsUnique().IsClustered();
        agent.Property(x => x.Verifier).HasColumnType("binary(32)").IsRequired();
        agent.Property(x => x.PendingVerifier).HasColumnType("binary(32)");
        agent.HasIndex(x => x.DeviceId).IsUnique().HasFilter("[RevokedAt] IS NULL");

        var enrollment = modelBuilder.Entity<Enrollment>();
        enrollment.ToTable("Enrollments", table => table.HasCheckConstraint("CK_Enrollments_Id_NotEmpty", "[Id] <> '00000000-0000-0000-0000-000000000000'"));
        enrollment.HasKey(x => x.Id).IsClustered(false);
        enrollment.Property(x => x.Id).ValueGeneratedNever();
        enrollment.Property(x => x.RowId).UseIdentityColumn();
        enrollment.HasIndex(x => x.RowId).IsUnique().IsClustered();
        enrollment.Property(x => x.Verifier).HasColumnType("binary(32)").IsRequired();
        enrollment.HasIndex(x => x.DeviceId);
        enrollment.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.NoAction);

        var audit = modelBuilder.Entity<AgentAudit>();
        audit.ToTable("AgentAudits");
        audit.HasKey(x => x.RowId).IsClustered();
        audit.Property(x => x.RowId).UseIdentityColumn();
        audit.Property(x => x.Action).HasMaxLength(32).IsRequired();
        audit.HasIndex(x => new { x.DeviceId, x.OccurredAt, x.RowId });
        audit.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.NoAction);

        var batch = modelBuilder.Entity<ReceivedBatch>();
        batch.ToTable("ReceivedBatches");
        batch.HasKey(x => x.RowId).IsClustered();
        batch.Property(x => x.RowId).UseIdentityColumn();
        batch.Property(x => x.Digest).HasColumnType("binary(32)").IsRequired();
        batch.HasIndex(x => new { x.AgentId, x.BatchId }).IsUnique();
        batch.HasIndex(x => new { x.AgentId, x.ReceivedAt, x.RowId });
        batch.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.NoAction);

        var observation = modelBuilder.Entity<RuntimeObservation>();
        observation.ToTable("RuntimeObservations");
        observation.HasKey(x => x.RowId).IsClustered();
        observation.Property(x => x.RowId).UseIdentityColumn();
        observation.Property(x => x.OperatingSystem).HasMaxLength(256).IsRequired();
        observation.Property(x => x.Architecture).HasMaxLength(32).IsRequired();
        observation.Property(x => x.RuntimeVersion).HasMaxLength(128).IsRequired();
        observation.HasIndex(x => new { x.AgentId, x.MessageId }).IsUnique();
        observation.HasIndex(x => new { x.AgentId, x.OccurredAt, x.RowId });
        observation.HasOne<Agent>().WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.NoAction);
    }
}
