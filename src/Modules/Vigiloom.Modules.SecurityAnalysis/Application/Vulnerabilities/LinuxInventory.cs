using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

internal enum LinuxInventoryStatus { Ready, Missing, Incomplete, Stale, Unsupported, Invalid }
internal sealed record LinuxInventoryEvidence(LinuxInventoryStatus Status, string? Ecosystem = null,
    Guid? HostSnapshotId = null, Guid? PackageSnapshotId = null, IReadOnlyList<InventoryEntry>? Packages = null);

/// <summary>Loads verified current inventory for the resolved Agent. Identity and generations must be rechecked when publishing findings.</summary>
internal static class LinuxInventory
{
    internal static async Task<LinuxInventoryEvidence> LoadAsync(SecurityAnalysisDbContext db, InventoryOwner owner,
        DateTimeOffset now, TimeSpan maximumAge, CancellationToken token, bool debian = false)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.AgentId == Guid.Empty || owner.DeviceId == Guid.Empty || owner.DeviceGeneration < 0 || maximumAge <= TimeSpan.Zero || maximumAge > TimeSpan.FromDays(30))
            throw new ArgumentException("A resolved Agent and bounded inventory age are required.");
        var host = await LoadSourceAsync(InventorySource.HostSystem);
        if (host.Status != LinuxInventoryStatus.Ready) return new(host.Status);
        var entries = host.Chunks!.SelectMany(c => c.Items).ToArray();
        if (entries.Length != 1) return new(LinuxInventoryStatus.Invalid);
        var system = entries[0];
        if (!string.Equals(system.OperatingSystemFamily, "Linux", StringComparison.Ordinal) ||
            !string.Equals(system.DistributionId, debian ? "debian" : "ubuntu", StringComparison.Ordinal)) return new(LinuxInventoryStatus.Unsupported);
        var ecosystem = debian ? DebianRelease.TrackerRelease(system.DistributionVersion) : UbuntuRelease.Ecosystem(system.DistributionVersion);
        if (ecosystem is null) return new(LinuxInventoryStatus.Unsupported);
        var dpkg = await LoadSourceAsync(InventorySource.LinuxDpkg);
        if (dpkg.Status != LinuxInventoryStatus.Ready) return new(dpkg.Status);
        if (dpkg.Snapshot!.SchemaVersion != InventoryChunkCodec.DpkgSourceSchemaVersion) return new(LinuxInventoryStatus.Incomplete);
        return new(LinuxInventoryStatus.Ready, ecosystem,
            host.Snapshot!.SnapshotId, dpkg.Snapshot.SnapshotId, dpkg.Chunks!.SelectMany(c => c.Items).ToArray());

        async Task<(LinuxInventoryStatus Status, InventorySnapshot? Snapshot, InventoryChunk[]? Chunks)> LoadSourceAsync(InventorySource source)
        {
            var state = await db.InventorySources.AsNoTracking().SingleOrDefaultAsync(x => x.AgentId == owner.AgentId && x.DeviceId == owner.DeviceId &&
                x.DeviceGeneration == owner.DeviceGeneration && x.Source == source, token);
            if (state?.CurrentGeneration is not long generation) return (LinuxInventoryStatus.Missing, null, null);
            if (state.LastStatus != InventorySnapshotStatus.Complete || state.LastOutcome != InventoryOutcome.Complete)
                return (LinuxInventoryStatus.Incomplete, null, null);
            var snapshot = await db.InventorySnapshots.AsNoTracking().SingleOrDefaultAsync(x => x.AgentId == owner.AgentId && x.Source == source &&
                x.Generation == generation && x.DeviceId == owner.DeviceId && x.DeviceGeneration == owner.DeviceGeneration, token);
            if (snapshot is null || snapshot.Status != InventorySnapshotStatus.Complete || snapshot.Outcome != InventoryOutcome.Complete)
                return (LinuxInventoryStatus.Incomplete, null, null);
            if (snapshot.ObservedAt > now.AddMinutes(5) || snapshot.ObservedAt < now.Subtract(maximumAge))
                return (LinuxInventoryStatus.Stale, null, null);
            var chunks = new List<InventoryChunk>(snapshot.ChunkCount);
            var payloads = db.InventoryChunks.AsNoTracking().Where(x => x.AgentId == owner.AgentId && x.Source == source && x.Generation == generation)
                .OrderBy(x => x.ChunkIndex).Select(x => x.Payload).AsAsyncEnumerable();
            await foreach (var payload in payloads.WithCancellation(token))
            {
                if (!InventoryChunkCodec.TryRead(payload, out var chunk) || !InventoryManifest.Matches(snapshot, owner, chunk!))
                    return (LinuxInventoryStatus.Invalid, null, null);
                chunks.Add(chunk!);
            }
            return SnapshotVerification.IsComplete(chunks) ? (LinuxInventoryStatus.Ready, snapshot, chunks.ToArray()) :
                (LinuxInventoryStatus.Invalid, null, null);
        }
    }
}
