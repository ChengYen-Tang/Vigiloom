using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Integrity;

/// <summary>Reads private scope status and the currently published baseline for a resolved Agent.</summary>
public sealed class IntegrityQuery(IDbContextFactory<SecurityAnalysisDbContext> contexts)
{
    public async Task<DeviceIntegrityView> GetAsync(Guid deviceId, Guid? agentId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var scopes = await db.IntegrityScopes.AsNoTracking().Where(x => x.DeviceId == deviceId && x.RetiredAt == null)
            .OrderBy(x => x.Id).ToArrayAsync(token);
        var scopeIds = scopes.Select(x => x.Id).ToArray();
        var states = agentId is null || scopeIds.Length == 0 ? [] : await (from state in db.IntegrityScopeStates.AsNoTracking()
            join scan in db.IntegrityScans.AsNoTracking() on
                new { state.AgentId, state.ScopeId, Generation = state.CurrentGeneration } equals
                new { scan.AgentId, scan.ScopeId, Generation = (long?)scan.Generation } into scans
            from scan in scans.DefaultIfEmpty()
            where state.AgentId == agentId && state.DeviceId == deviceId && scopeIds.Contains(state.ScopeId)
            select new { state, scan }).ToArrayAsync(token);
        var byScope = states.ToDictionary(x => x.state.ScopeId);
        var results = new List<IntegrityScopeMonitoringView>(scopes.Length);
        foreach (var setting in scopes)
        {
            var scope = IntegrityConfiguration.View(setting).Scope;
            byScope.TryGetValue(scope.Id, out var status);
            results.Add(new(scope.Id, scope.Revision, scope.Kind, scope.Root, scope.Enabled, status?.state.LastAttemptAt,
                status?.state.LastOutcome, status?.state.LastStatus, status?.state.LastPartialReason, status?.state.LastCoverageGap,
                status?.state.LastCompleteAt, status?.scan?.SnapshotId, status?.scan?.ScopeRevision, status?.scan?.ObservedAt,
                status?.scan?.ChunkCount, status?.scan?.TotalItems));
        }
        return new(deviceId, agentId, results);
    }

    public async Task<IntegritySnapshotChunkView?> GetChunkAsync(Guid deviceId, Guid agentId, Guid scopeId,
        Guid snapshotId, int chunkIndex, CancellationToken token)
    {
        if (deviceId.Version != 7 || agentId.Version != 7 || scopeId.Version != 7 || snapshotId.Version != 7 ||
            chunkIndex is < 0 or >= IntegrityChunkCodec.MaximumChunks) throw new ArgumentException("Invalid integrity snapshot query.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var payload = await (from state in db.IntegrityScopeStates.AsNoTracking()
            join scope in db.IntegrityScopes.AsNoTracking() on state.ScopeId equals scope.Id
            join scan in db.IntegrityScans.AsNoTracking() on new { state.AgentId, state.ScopeId, Generation = state.CurrentGeneration }
                equals new { scan.AgentId, scan.ScopeId, Generation = (long?)scan.Generation }
            join chunk in db.IntegrityChunks.AsNoTracking() on new { scan.AgentId, scan.ScopeId, scan.Generation }
                equals new { chunk.AgentId, chunk.ScopeId, chunk.Generation }
            where state.DeviceId == deviceId && scope.DeviceId == deviceId && scope.RetiredAt == null && state.AgentId == agentId &&
                state.ScopeId == scopeId && scan.SnapshotId == snapshotId && chunk.ChunkIndex == chunkIndex
            select chunk.Payload).SingleOrDefaultAsync(token);
        if (payload is null) return null;
        if (!IntegrityChunkCodec.TryRead(payload, out var parsed) || parsed!.SnapshotId != snapshotId ||
            parsed.ScopeId != scopeId || parsed.ChunkIndex != chunkIndex) throw new InvalidDataException("The saved integrity chunk is invalid.");
        return new(parsed.SnapshotId, parsed.ScopeId, parsed.ScopeRevision, parsed.ObservedAt, parsed.ChunkIndex, parsed.ChunkCount, parsed.Items);
    }
}
