using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Modules.SecurityAnalysis.Domain.Integrity;

/// <summary>Durable generation fence and latest observed state for one authenticated Agent and scope.</summary>
public sealed class IntegrityScopeState
{
    public long RowId { get; private set; }
    public Guid AgentId { get; set; }
    public Guid DeviceId { get; set; }
    public long DeviceGeneration { get; set; }
    public Guid ScopeId { get; set; }
    public long ClosedGeneration { get; set; }
    public long? ActiveGeneration { get; set; }
    public long? CurrentGeneration { get; set; }
    public long? LastAttemptGeneration { get; set; }
    public Guid? LastSnapshotId { get; set; }
    public IntegrityScanOutcome? LastOutcome { get; set; }
    public IntegrityCompletion? LastStatus { get; set; }
    public IntegrityPartialReason? LastPartialReason { get; set; }
    public bool? LastCoverageGap { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastCompleteAt { get; set; }
}
