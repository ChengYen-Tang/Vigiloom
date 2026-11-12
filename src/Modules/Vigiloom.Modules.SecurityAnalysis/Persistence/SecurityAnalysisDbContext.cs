using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Domain.Integrity;

namespace Vigiloom.Modules.SecurityAnalysis.Persistence;

public sealed class SecurityAnalysisDbContext(DbContextOptions<SecurityAnalysisDbContext> options) : DbContext(options)
{
    public DbSet<InventorySourceState> InventorySources => Set<InventorySourceState>();
    public DbSet<InventorySnapshot> InventorySnapshots => Set<InventorySnapshot>();
    public DbSet<InventoryChunkData> InventoryChunks => Set<InventoryChunkData>();
    public DbSet<InventoryChangeRecord> InventoryChanges => Set<InventoryChangeRecord>();
    public DbSet<InventorySetting> InventorySettings => Set<InventorySetting>();
    public DbSet<InventorySettingAudit> InventorySettingAudits => Set<InventorySettingAudit>();
    public DbSet<IntegrityScopeSetting> IntegrityScopes => Set<IntegrityScopeSetting>();
    public DbSet<IntegrityScopeAudit> IntegrityScopeAudits => Set<IntegrityScopeAudit>();
    public DbSet<IntegrityPolicyState> IntegrityPolicyStates => Set<IntegrityPolicyState>();
    public DbSet<IntegrityScopeState> IntegrityScopeStates => Set<IntegrityScopeState>();
    public DbSet<IntegrityScan> IntegrityScans => Set<IntegrityScan>();
    public DbSet<IntegrityChunkData> IntegrityChunks => Set<IntegrityChunkData>();
    public DbSet<IntegrityChangeRecord> IntegrityChanges => Set<IntegrityChangeRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("security");
        var source = modelBuilder.Entity<InventorySourceState>();
        source.ToTable("InventorySources", table =>
        {
            table.HasCheckConstraint("CK_InventorySources_Source", "[Source] BETWEEN 1 AND 15");
            table.HasCheckConstraint("CK_InventorySources_Generations", "[ClosedGeneration] >= 0 AND ([ActiveGeneration] IS NULL OR [ActiveGeneration] > [ClosedGeneration]) AND ([CurrentGeneration] IS NULL OR ([CurrentGeneration] > 0 AND [CurrentGeneration] <= [ClosedGeneration]))");
            table.HasCheckConstraint("CK_InventorySources_DeviceGeneration", "[DeviceGeneration] >= 0");
            table.HasCheckConstraint("CK_InventorySources_Ids", "[AgentId] <> '00000000-0000-0000-0000-000000000000' AND [DeviceId] <> '00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_InventorySources_LastAttempt", "([LastAttemptGeneration] IS NULL AND [LastAttemptAt] IS NULL AND [LastOutcome] IS NULL AND [LastStatus] IS NULL) OR ([LastAttemptGeneration] IS NOT NULL AND [LastAttemptGeneration] > 0 AND [LastAttemptAt] IS NOT NULL AND [LastOutcome] IS NOT NULL AND [LastOutcome] BETWEEN 1 AND 5 AND [LastStatus] IS NOT NULL AND [LastStatus] BETWEEN 2 AND 5)");
            table.HasCheckConstraint("CK_InventorySources_RejectionReason", "([LastStatus] IS NOT NULL AND [LastStatus] = 5 AND [LastRejectionReason] IS NOT NULL AND [LastRejectionReason] BETWEEN 1 AND 3) OR (([LastStatus] IS NULL OR [LastStatus] <> 5) AND [LastRejectionReason] IS NULL)");
        });
        source.HasKey(x => x.RowId).IsClustered();
        source.Property(x => x.RowId).UseIdentityColumn();
        source.HasAlternateKey(x => new { x.AgentId, x.Source }).IsClustered(false);
        source.HasIndex(x => new { x.DeviceId, x.Source });

        var snapshot = modelBuilder.Entity<InventorySnapshot>();
        snapshot.ToTable("InventorySnapshots", table =>
        {
            table.HasCheckConstraint("CK_InventorySnapshots_Generation", "[Generation] > 0 AND [DeviceGeneration] >= 0 AND [SchemaVersion] > 0");
            table.HasCheckConstraint("CK_InventorySnapshots_Ids", "[AgentId] <> '00000000-0000-0000-0000-000000000000' AND [DeviceId] <> '00000000-0000-0000-0000-000000000000' AND [SnapshotId] <> '00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_InventorySnapshots_Limits", "[ChunkCount] BETWEEN 1 AND 1024 AND [TotalItems] BETWEEN 0 AND 20000 AND [TotalBytes] BETWEEN 2 AND 33554432 AND [ReceivedChunks] BETWEEN 0 AND [ChunkCount] AND [ReceivedItems] BETWEEN 0 AND [TotalItems] AND [ReceivedBytes] BETWEEN 0 AND [TotalBytes]");
            table.HasCheckConstraint("CK_InventorySnapshots_Status", "[Status] BETWEEN 1 AND 5 AND [Outcome] BETWEEN 1 AND 5 AND (([Status] = 1 AND [FinishedAt] IS NULL) OR ([Status] <> 1 AND [FinishedAt] IS NOT NULL))");
            table.HasCheckConstraint("CK_InventorySnapshots_Complete", "[Status] <> 2 OR ([Outcome] = 1 AND [ReceivedChunks] = [ChunkCount] AND [ReceivedItems] = [TotalItems] AND [ReceivedBytes] = [TotalBytes])");
            table.HasCheckConstraint("CK_InventorySnapshots_Partial", "[Status] <> 3 OR [Outcome] <> 1");
        });
        snapshot.HasKey(x => x.RowId).IsClustered();
        snapshot.Property(x => x.RowId).UseIdentityColumn();
        snapshot.Property(x => x.Digest).HasColumnType("binary(32)").IsRequired();
        snapshot.HasAlternateKey(x => new { x.AgentId, x.Source, x.Generation }).IsClustered(false);
        snapshot.HasIndex(x => new { x.AgentId, x.SnapshotId }).IsUnique();
        snapshot.HasIndex(x => new { x.Status, x.StartedAt, x.RowId });
        snapshot.HasOne<InventorySourceState>().WithMany().HasForeignKey(x => new { x.AgentId, x.Source })
            .HasPrincipalKey(x => new { x.AgentId, x.Source }).OnDelete(DeleteBehavior.NoAction);

        var chunk = modelBuilder.Entity<InventoryChunkData>();
        chunk.ToTable("InventoryChunks", table =>
        {
            table.HasCheckConstraint("CK_InventoryChunks_Index", "[Generation] > 0 AND [ChunkIndex] BETWEEN 0 AND 1023");
            table.HasCheckConstraint("CK_InventoryChunks_Size", "DATALENGTH([Payload]) BETWEEN 1 AND 1048576");
        });
        chunk.HasKey(x => x.RowId).IsClustered();
        chunk.Property(x => x.RowId).UseIdentityColumn();
        chunk.Property(x => x.Payload).IsRequired();
        chunk.HasIndex(x => new { x.AgentId, x.Source, x.Generation, x.ChunkIndex }).IsUnique();
        chunk.HasOne<InventorySnapshot>().WithMany().HasForeignKey(x => new { x.AgentId, x.Source, x.Generation })
            .HasPrincipalKey(x => new { x.AgentId, x.Source, x.Generation }).OnDelete(DeleteBehavior.NoAction);

        var change = modelBuilder.Entity<InventoryChangeRecord>();
        change.ToTable("InventoryChanges", table =>
        {
            table.HasCheckConstraint("CK_InventoryChanges_Values", "[Generation] > 0 AND [Source] BETWEEN 1 AND 15 AND [Kind] BETWEEN 1 AND 5");
            table.HasCheckConstraint("CK_InventoryChanges_Content", "([Kind] IN (1, 4) AND [BeforeJson] IS NULL AND [AfterJson] IS NOT NULL) OR ([Kind] IN (2, 5) AND [BeforeJson] IS NOT NULL AND [AfterJson] IS NOT NULL) OR ([Kind] = 3 AND [BeforeJson] IS NOT NULL AND [AfterJson] IS NULL)");
        });
        change.HasKey(x => x.RowId).IsClustered();
        change.Property(x => x.RowId).UseIdentityColumn();
        change.Property(x => x.RecordedAt).HasDefaultValueSql("TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')");
        change.Property(x => x.Key).HasMaxLength(2048).IsRequired();
        change.HasIndex(x => new { x.DeviceId, x.Source, x.ObservedAt, x.RowId });
        change.HasIndex(x => new { x.DeviceId, x.ObservedAt, x.RowId });
        change.HasIndex(x => new { x.RecordedAt, x.RowId });

        var setting = modelBuilder.Entity<InventorySetting>();
        setting.ToTable("InventorySettings", table =>
        {
            table.HasCheckConstraint("CK_InventorySettings_Id", "[Id] <> '00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_InventorySettings_Interval", "[ScanIntervalSeconds] IS NULL OR [ScanIntervalSeconds] BETWEEN 900 AND 604800");
        });
        setting.HasKey(x => x.Id).IsClustered(false);
        setting.Property(x => x.Id).ValueGeneratedNever();
        setting.Property(x => x.RowId).UseIdentityColumn();
        setting.HasIndex(x => x.RowId).IsUnique().IsClustered();
        setting.HasIndex(x => x.DeviceId).IsUnique().HasFilter(null);
        setting.Property(x => x.Version).IsRowVersion();

        var settingAudit = modelBuilder.Entity<InventorySettingAudit>();
        settingAudit.ToTable("InventorySettingAudits");
        settingAudit.HasKey(x => x.RowId).IsClustered();
        settingAudit.Property(x => x.RowId).UseIdentityColumn();
        settingAudit.Property(x => x.EffectiveRevision).HasMaxLength(64).IsRequired();
        settingAudit.Property(x => x.ChangeVersion).HasColumnType("binary(8)").IsRequired();
        settingAudit.HasIndex(x => new { x.DeviceId, x.OccurredAt, x.RowId });
        settingAudit.HasIndex(x => new { x.OccurredAt, x.RowId });
        settingAudit.HasOne<InventorySetting>().WithMany().HasForeignKey(x => x.SettingId).OnDelete(DeleteBehavior.NoAction);

        var integrity = modelBuilder.Entity<IntegrityScopeSetting>();
        integrity.ToTable("IntegrityScopes", table =>
        {
            table.HasCheckConstraint("CK_IntegrityScopes_Ids", "[Id] <> '00000000-0000-0000-0000-000000000000' AND [DeviceId] <> '00000000-0000-0000-0000-000000000000' AND [UpdatedBy] <> '00000000-0000-0000-0000-000000000000'");
            table.HasCheckConstraint("CK_IntegrityScopes_Revision", "[Revision] > 0");
            table.HasCheckConstraint("CK_IntegrityScopes_Definition", "ISJSON([DefinitionJson]) = 1 AND DATALENGTH([DefinitionJson]) BETWEEN 2 AND 65536");
        });
        integrity.HasKey(x => x.Id).IsClustered(false);
        integrity.Property(x => x.Id).ValueGeneratedNever();
        integrity.Property(x => x.RowId).UseIdentityColumn();
        integrity.HasIndex(x => x.RowId).IsUnique().IsClustered();
        integrity.HasIndex(x => new { x.DeviceId, x.RetiredAt });
        integrity.Property(x => x.DefinitionJson).HasMaxLength(32768).IsRequired();
        integrity.Property(x => x.Version).IsRowVersion();

        var integrityAudit = modelBuilder.Entity<IntegrityScopeAudit>();
        integrityAudit.ToTable("IntegrityScopeAudits", table =>
        {
            table.HasCheckConstraint("CK_IntegrityScopeAudits_Action", "[Action] BETWEEN 1 AND 3 AND [Revision] > 0");
        });
        integrityAudit.HasKey(x => x.RowId).IsClustered();
        integrityAudit.Property(x => x.RowId).UseIdentityColumn();
        integrityAudit.HasIndex(x => new { x.DeviceId, x.OccurredAt, x.RowId });
        integrityAudit.HasOne<IntegrityScopeSetting>().WithMany().HasForeignKey(x => x.ScopeId).OnDelete(DeleteBehavior.NoAction);

        var integrityPolicy = modelBuilder.Entity<IntegrityPolicyState>();
        integrityPolicy.ToTable("IntegrityPolicyStates", table =>
        {
            table.HasCheckConstraint("CK_IntegrityPolicyStates_Generation", "[Generation] > 0 AND [DeviceId] <> '00000000-0000-0000-0000-000000000000'");
        });
        integrityPolicy.HasKey(x => x.RowId).IsClustered();
        integrityPolicy.Property(x => x.RowId).UseIdentityColumn();
        integrityPolicy.HasIndex(x => x.DeviceId).IsUnique();

        var integrityState = modelBuilder.Entity<IntegrityScopeState>();
        integrityState.ToTable("IntegrityScopeStates", table =>
        {
            table.HasCheckConstraint("CK_IntegrityScopeStates_Ids", "[AgentId] <> '00000000-0000-0000-0000-000000000000' AND [DeviceId] <> '00000000-0000-0000-0000-000000000000' AND [ScopeId] <> '00000000-0000-0000-0000-000000000000' AND [DeviceGeneration] >= 0");
            table.HasCheckConstraint("CK_IntegrityScopeStates_Generations", "[ClosedGeneration] >= 0 AND ([ActiveGeneration] IS NULL OR [ActiveGeneration] > [ClosedGeneration]) AND ([CurrentGeneration] IS NULL OR ([CurrentGeneration] > 0 AND [CurrentGeneration] <= [ClosedGeneration]))");
        });
        integrityState.HasKey(x => x.RowId).IsClustered();
        integrityState.Property(x => x.RowId).UseIdentityColumn();
        integrityState.HasAlternateKey(x => new { x.AgentId, x.ScopeId }).IsClustered(false);
        integrityState.HasIndex(x => new { x.DeviceId, x.ScopeId });
        integrityState.HasOne<IntegrityScopeSetting>().WithMany().HasForeignKey(x => x.ScopeId).OnDelete(DeleteBehavior.NoAction);

        var integrityScan = modelBuilder.Entity<IntegrityScan>();
        integrityScan.ToTable("IntegrityScans", table =>
        {
            table.HasCheckConstraint("CK_IntegrityScans_Values", "[Generation] > 0 AND [ScopeRevision] > 0 AND [DeviceGeneration] >= 0 AND [Outcome] BETWEEN 1 AND 5 AND [Status] BETWEEN 1 AND 5");
            table.HasCheckConstraint("CK_IntegrityScans_Limits", "[ChunkCount] BETWEEN 1 AND 1024 AND [TotalItems] BETWEEN 0 AND 20000 AND [TotalBytes] BETWEEN 2 AND 33554432 AND [ReceivedChunks] BETWEEN 0 AND [ChunkCount] AND [ReceivedItems] BETWEEN 0 AND [TotalItems] AND [ReceivedBytes] BETWEEN 0 AND [TotalBytes]");
            table.HasCheckConstraint("CK_IntegrityScans_Terminal", "([Status] = 1 AND [FinishedAt] IS NULL) OR ([Status] <> 1 AND [FinishedAt] IS NOT NULL)");
            table.HasCheckConstraint("CK_IntegrityScans_Complete", "[Status] <> 2 OR ([Outcome] = 1 AND [ReceivedChunks] = [ChunkCount] AND [ReceivedItems] = [TotalItems] AND [ReceivedBytes] = [TotalBytes])");
        });
        integrityScan.HasKey(x => x.RowId).IsClustered();
        integrityScan.Property(x => x.RowId).UseIdentityColumn();
        integrityScan.Property(x => x.Digest).HasColumnType("binary(32)").IsRequired();
        integrityScan.HasAlternateKey(x => new { x.AgentId, x.ScopeId, x.Generation }).IsClustered(false);
        integrityScan.HasIndex(x => new { x.AgentId, x.SnapshotId }).IsUnique();
        integrityScan.HasIndex(x => new { x.Status, x.StartedAt, x.RowId });
        integrityScan.HasOne<IntegrityScopeState>().WithMany().HasForeignKey(x => new { x.AgentId, x.ScopeId })
            .HasPrincipalKey(x => new { x.AgentId, x.ScopeId }).OnDelete(DeleteBehavior.NoAction);

        var integrityChunk = modelBuilder.Entity<IntegrityChunkData>();
        integrityChunk.ToTable("IntegrityChunks", table =>
        {
            table.HasCheckConstraint("CK_IntegrityChunks_Values", "[Generation] > 0 AND [ChunkIndex] BETWEEN 0 AND 1023 AND DATALENGTH([Payload]) BETWEEN 1 AND 1048576");
        });
        integrityChunk.HasKey(x => x.RowId).IsClustered();
        integrityChunk.Property(x => x.RowId).UseIdentityColumn();
        integrityChunk.Property(x => x.Payload).IsRequired();
        integrityChunk.HasIndex(x => new { x.AgentId, x.ScopeId, x.Generation, x.ChunkIndex }).IsUnique();
        integrityChunk.HasOne<IntegrityScan>().WithMany().HasForeignKey(x => new { x.AgentId, x.ScopeId, x.Generation })
            .HasPrincipalKey(x => new { x.AgentId, x.ScopeId, x.Generation }).OnDelete(DeleteBehavior.NoAction);

        var integrityChange = modelBuilder.Entity<IntegrityChangeRecord>();
        integrityChange.ToTable("IntegrityChanges", table =>
        {
            table.HasCheckConstraint("CK_IntegrityChanges_Values", "[ScopeRevision] > 0 AND [Generation] > 0 AND [Kind] BETWEEN 1 AND 3");
            table.HasCheckConstraint("CK_IntegrityChanges_Content", "([Kind] = 1 AND [BeforeJson] IS NULL AND [AfterJson] IS NOT NULL) OR ([Kind] = 2 AND [BeforeJson] IS NOT NULL AND [AfterJson] IS NOT NULL) OR ([Kind] = 3 AND [BeforeJson] IS NOT NULL AND [AfterJson] IS NULL)");
        });
        integrityChange.HasKey(x => x.RowId).IsClustered();
        integrityChange.Property(x => x.RowId).UseIdentityColumn();
        integrityChange.Property(x => x.Key).HasMaxLength(2048).IsRequired();
        integrityChange.HasIndex(x => new { x.DeviceId, x.ObservedAt, x.RowId });
        integrityChange.HasIndex(x => new { x.DeviceId, x.ScopeId, x.ObservedAt, x.RowId });
        integrityChange.HasIndex(x => new { x.AgentId, x.ScopeId, x.ObservedAt, x.RowId });
        integrityChange.HasIndex(x => new { x.RecordedAt, x.RowId });
    }
}
