using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Modules.SecurityAnalysis.Domain.Integrity;

public enum IntegrityScanStatus { Staging = 1, Complete = 2, Partial = 3, Rejected = 4, Expired = 5 }

/// <summary>Immutable scan manifest and its bounded staging lifecycle.</summary>
public sealed class IntegrityScan
{
    public long RowId { get; private set; }
    public Guid AgentId { get; set; }
    public Guid DeviceId { get; set; }
    public long DeviceGeneration { get; set; }
    public Guid ScopeId { get; set; }
    public long ScopeRevision { get; set; }
    public Guid SnapshotId { get; set; }
    public long Generation { get; set; }
    public DateTimeOffset EnqueuedAt { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public IntegrityScanOutcome Outcome { get; set; }
    public bool CoverageGap { get; set; }
    public IntegrityPartialReason? PartialReason { get; set; }
    public int ChunkCount { get; set; }
    public int TotalItems { get; set; }
    public long TotalBytes { get; set; }
    public byte[] Digest { get; set; } = [];
    public int ReceivedChunks { get; set; }
    public int ReceivedItems { get; set; }
    public long ReceivedBytes { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public IntegrityScanStatus Status { get; set; }
}
