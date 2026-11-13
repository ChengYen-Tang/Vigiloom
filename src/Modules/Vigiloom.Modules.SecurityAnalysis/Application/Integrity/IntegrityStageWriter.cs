using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Domain.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Integrity;

/// <summary>Stages and publishes a scope scan inside the authenticated receiver's transaction.</summary>
public sealed class IntegrityStageWriter : IIntegrityStageWriter
{
    public async Task<IntegrityStageResult> StageAsync(DbConnection connection, DbTransaction transaction, IntegrityOwner owner,
        IntegrityChunk chunk, DateTimeOffset databaseNow, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.AgentId.Version != 7 || owner.DeviceId.Version != 7 || owner.DeviceGeneration < 0)
            throw new ArgumentException("A registered Agent and device generation are required.", nameof(owner));
        if (databaseNow == default || databaseNow.Offset != TimeSpan.Zero) throw new ArgumentException("Database time must be UTC.", nameof(databaseNow));
        if (!await AgentSnapshotLock.TryAcquireAsync(connection, transaction, "Integrity", owner.AgentId, 5000, token))
            return IntegrityStageResult.CapacityExceeded;
        var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
        await using var db = new SecurityAnalysisDbContext(options);
        await db.Database.UseTransactionAsync(transaction, token);
        var setting = await db.IntegrityScopes.FromSqlInterpolated($"SELECT * FROM [security].[IntegrityScopes] WITH (HOLDLOCK) WHERE [Id] = {chunk.ScopeId} AND [DeviceId] = {owner.DeviceId} AND [RetiredAt] IS NULL")
            .AsNoTracking().SingleOrDefaultAsync(token);
        if (setting is null || setting.Revision != chunk.ScopeRevision) return IntegrityStageResult.Conflict;
        var policy = IntegrityConfiguration.View(setting).Scope;
        if (!policy.Enabled || chunk.TotalItems > policy.MaximumEntries || chunk.Items.Any(x => policy.Kind switch
            {
                IntegrityScopeKind.Directory => x.Kind is not (IntegrityItemKind.File or IntegrityItemKind.Directory),
                IntegrityScopeKind.Registry => x.Kind is not (IntegrityItemKind.RegistryKey or IntegrityItemKind.RegistryValue),
                _ => true
            })) return IntegrityStageResult.Conflict;
        var state = await db.IntegrityScopeStates.SingleOrDefaultAsync(x => x.AgentId == owner.AgentId && x.ScopeId == chunk.ScopeId, token);
        if (state is not null && (state.DeviceId != owner.DeviceId || state.DeviceGeneration != owner.DeviceGeneration)) return IntegrityStageResult.Conflict;
        if (state is not null && chunk.Generation <= state.ClosedGeneration) return IntegrityStageResult.Expired;
        if (await db.IntegrityScopeStates.AnyAsync(x => x.AgentId == owner.AgentId && x.ScopeId != chunk.ScopeId && x.ActiveGeneration != null, token))
            return IntegrityStageResult.CapacityExceeded;
        if (state?.ActiveGeneration is long active && chunk.Generation < active) return IntegrityStageResult.Expired;
        if (state?.ActiveGeneration is long previous && chunk.Generation > previous)
        {
            var stale = await db.IntegrityScans.SingleAsync(x => x.AgentId == owner.AgentId && x.ScopeId == chunk.ScopeId && x.Generation == previous, token);
            await CloseAsync(db, state, stale, IntegrityScanStatus.Expired, databaseNow, token);
        }
        var scan = state?.ActiveGeneration is null ? null : await db.IntegrityScans.SingleAsync(x =>
            x.AgentId == owner.AgentId && x.ScopeId == chunk.ScopeId && x.Generation == chunk.Generation, token);
        if (scan is null)
        {
            if (state is null)
            {
                state = new IntegrityScopeState { AgentId = owner.AgentId, DeviceId = owner.DeviceId,
                    DeviceGeneration = owner.DeviceGeneration, ScopeId = chunk.ScopeId };
                db.IntegrityScopeStates.Add(state);
            }
            var currentBytes = await (from source in db.IntegrityScopeStates
                join current in db.IntegrityScans on
                    new { source.AgentId, source.ScopeId, Generation = source.CurrentGeneration } equals
                    new { current.AgentId, current.ScopeId, Generation = (long?)current.Generation }
                where source.AgentId == owner.AgentId && source.ScopeId != chunk.ScopeId
                select (long?)current.TotalBytes).SumAsync(token) ?? 0;
            scan = CreateScan(owner, chunk, databaseNow);
            db.IntegrityScans.Add(scan);
            if (chunk.Outcome == IntegrityScanOutcome.Complete && currentBytes + chunk.TotalBytes > IntegrityChunkCodec.MaximumSnapshotBytes)
            {
                await CloseAsync(db, state, scan, IntegrityScanStatus.Rejected, databaseNow, token);
                return IntegrityStageResult.SnapshotTooLarge;
            }
            state.ActiveGeneration = chunk.Generation;
        }
        else if (!IntegrityManifest.Matches(scan, owner, chunk)) return IntegrityStageResult.Conflict;

        var payload = IntegrityChunkCodec.Serialize(chunk);
        var existing = await db.IntegrityChunks.SingleOrDefaultAsync(x => x.AgentId == owner.AgentId && x.ScopeId == chunk.ScopeId &&
            x.Generation == chunk.Generation && x.ChunkIndex == chunk.ChunkIndex, token);
        if (existing is not null) return existing.Payload.AsSpan().SequenceEqual(payload) ? IntegrityStageResult.Accepted : IntegrityStageResult.Conflict;
        var measured = IntegrityDigest.MeasureChunk(chunk.Items);
        if (scan.ReceivedChunks >= scan.ChunkCount || chunk.Items.Count > scan.TotalItems - scan.ReceivedItems ||
            measured.Bytes > scan.TotalBytes - scan.ReceivedBytes) return IntegrityStageResult.Conflict;
        db.IntegrityChunks.Add(new IntegrityChunkData { AgentId = owner.AgentId, ScopeId = chunk.ScopeId,
            Generation = chunk.Generation, ChunkIndex = chunk.ChunkIndex, Payload = payload });
        scan.ReceivedChunks++;
        scan.ReceivedItems += chunk.Items.Count;
        scan.ReceivedBytes += measured.Bytes;
        await db.SaveChangesAsync(token);
        if (scan.ReceivedChunks == scan.ChunkCount)
        {
            if (!InventoryPublicationGate.Shared.TryEnter()) return IntegrityStageResult.CapacityExceeded;
            try { await PublishAsync(db, state!, scan, owner, policy, databaseNow, token); }
            finally { InventoryPublicationGate.Shared.Exit(); }
        }
        return IntegrityStageResult.Accepted;
    }

    private static IntegrityScan CreateScan(IntegrityOwner owner, IntegrityChunk chunk, DateTimeOffset now) => new()
    {
        AgentId = owner.AgentId, DeviceId = owner.DeviceId, DeviceGeneration = owner.DeviceGeneration,
        ScopeId = chunk.ScopeId, ScopeRevision = chunk.ScopeRevision, SnapshotId = chunk.SnapshotId,
        Generation = chunk.Generation, EnqueuedAt = chunk.EnqueuedAt, ObservedAt = chunk.ObservedAt,
        Outcome = chunk.Outcome, CoverageGap = chunk.CoverageGap, PartialReason = chunk.PartialReason,
        ChunkCount = chunk.ChunkCount, TotalItems = chunk.TotalItems, TotalBytes = chunk.TotalBytes,
        Digest = chunk.Digest.ToArray(), StartedAt = now, Status = IntegrityScanStatus.Staging
    };

    private static async Task PublishAsync(SecurityAnalysisDbContext db, IntegrityScopeState state, IntegrityScan scan,
        IntegrityOwner owner, IntegrityScope policy, DateTimeOffset now, CancellationToken token)
    {
        var comparer = policy.Kind == IntegrityScopeKind.Registry ? IntegrityChanges.RegistryKeys : StringComparer.Ordinal;
        var incoming = await ReadVerifiedAsync(db, scan, owner, comparer, token);
        var status = incoming is null ? IntegrityScanStatus.Rejected :
            scan.Outcome == IntegrityScanOutcome.Complete ? IntegrityScanStatus.Complete : IntegrityScanStatus.Partial;
        if (status == IntegrityScanStatus.Complete)
        {
            if (state.CurrentGeneration is long previous)
            {
                var old = await db.IntegrityScans.SingleAsync(x => x.AgentId == owner.AgentId && x.ScopeId == scan.ScopeId && x.Generation == previous, token);
                if (old.Status != IntegrityScanStatus.Complete) throw new InvalidOperationException("Current integrity baseline is incomplete.");
                var prior = await ReadVerifiedAsync(db, old, owner, comparer, token) ?? throw new InvalidOperationException("Current integrity baseline failed verification.");
                var comparison = IntegrityChanges.Compare(new(scan.ScopeId, old.ScopeRevision, true, prior.SelectMany(x => x.Items).ToArray()),
                    new(scan.ScopeId, scan.ScopeRevision, true, incoming!.SelectMany(x => x.Items).ToArray()),
                    comparer);
                var pending = new List<IntegrityChangeRecord>(128);
                foreach (var change in comparison.Changes)
                {
                    pending.Add(new IntegrityChangeRecord { AgentId = owner.AgentId, DeviceId = owner.DeviceId, ScopeId = scan.ScopeId,
                        ScopeRevision = scan.ScopeRevision, Generation = scan.Generation, ObservedAt = scan.ObservedAt, RecordedAt = now,
                        Kind = (int)change.Kind, Key = change.After?.Key ?? change.Before!.Key,
                        BeforeJson = change.Before is null ? null : JsonSerializer.Serialize(change.Before),
                        AfterJson = change.After is null ? null : JsonSerializer.Serialize(change.After), CoverageGap = scan.CoverageGap });
                    if (pending.Count == 128) await SaveChangesAsync();
                }
                if (pending.Count > 0) await SaveChangesAsync();
                await DeleteChunksAsync(db, old, token);
                db.IntegrityScans.Remove(old);

                async Task SaveChangesAsync()
                {
                    db.IntegrityChanges.AddRange(pending);
                    await db.SaveChangesAsync(token);
                    foreach (var entry in pending) db.Entry(entry).State = EntityState.Detached;
                    pending.Clear();
                }
            }
            state.CurrentGeneration = scan.Generation;
            state.LastCompleteAt = scan.ObservedAt;
        }
        else await DeleteChunksAsync(db, scan, token);
        await CloseAsync(db, state, scan, status, now, token);
    }

    private static async Task<IntegrityChunk[]?> ReadVerifiedAsync(SecurityAnalysisDbContext db, IntegrityScan scan, IntegrityOwner owner, StringComparer comparer, CancellationToken token)
    {
        var chunks = new List<IntegrityChunk>(scan.ChunkCount);
        var payloads = db.IntegrityChunks.AsNoTracking().Where(x => x.AgentId == scan.AgentId && x.ScopeId == scan.ScopeId && x.Generation == scan.Generation)
            .OrderBy(x => x.ChunkIndex).Select(x => x.Payload).AsAsyncEnumerable();
        await foreach (var payload in payloads.WithCancellation(token))
        {
            if (!IntegrityChunkCodec.TryRead(payload, out var chunk)) return null;
            chunks.Add(chunk!);
        }
        return IntegrityManifest.Verify(scan, chunks, owner, comparer) ? chunks.ToArray() : null;
    }

    internal static async Task CloseAsync(SecurityAnalysisDbContext db, IntegrityScopeState state, IntegrityScan scan,
        IntegrityScanStatus status, DateTimeOffset now, CancellationToken token)
    {
        if (state.LastAttemptGeneration is long previous && previous != state.CurrentGeneration && previous != scan.Generation)
        {
            var old = await db.IntegrityScans.SingleOrDefaultAsync(x => x.AgentId == state.AgentId && x.ScopeId == state.ScopeId && x.Generation == previous, token);
            if (old is not null) { await DeleteChunksAsync(db, old, token); db.IntegrityScans.Remove(old); }
        }
        if (status == IntegrityScanStatus.Expired) await DeleteChunksAsync(db, scan, token);
        scan.Status = status;
        scan.FinishedAt = now;
        state.ClosedGeneration = scan.Generation;
        state.ActiveGeneration = null;
        state.LastAttemptGeneration = scan.Generation;
        state.LastSnapshotId = scan.SnapshotId;
        state.LastOutcome = scan.Outcome;
        state.LastStatus = status switch
        {
            IntegrityScanStatus.Complete => IntegrityCompletion.Complete,
            IntegrityScanStatus.Partial => IntegrityCompletion.Partial,
            IntegrityScanStatus.Rejected => IntegrityCompletion.Rejected,
            IntegrityScanStatus.Expired => IntegrityCompletion.Expired,
            _ => throw new InvalidOperationException("Only terminal scans can be closed.")
        };
        state.LastPartialReason = scan.PartialReason;
        state.LastCoverageGap = scan.CoverageGap;
        state.LastAttemptAt = now;
        await db.SaveChangesAsync(token);
    }

    internal static Task<int> DeleteChunksAsync(SecurityAnalysisDbContext db, IntegrityScan scan, CancellationToken token) =>
        db.IntegrityChunks.Where(x => x.AgentId == scan.AgentId && x.ScopeId == scan.ScopeId && x.Generation == scan.Generation).ExecuteDeleteAsync(token);
}
