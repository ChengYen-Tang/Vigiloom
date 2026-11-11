using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;

/// <summary>Durable publication and closed-generation fence for one authenticated Agent source.</summary>
public sealed class InventorySourceState
{
    private InventorySourceState() { }
    public InventorySourceState(InventoryOwner owner, InventorySource source)
    {
        AgentId = owner.AgentId; DeviceId = owner.DeviceId; DeviceGeneration = owner.DeviceGeneration; Source = source;
    }

    public long RowId { get; private set; }
    public Guid AgentId { get; private set; }
    public Guid DeviceId { get; private set; }
    public long DeviceGeneration { get; private set; }
    public InventorySource Source { get; private set; }
    public long ClosedGeneration { get; internal set; }
    public long? ActiveGeneration { get; internal set; }
    public long? CurrentGeneration { get; internal set; }
    public DateTimeOffset? LastAttemptAt { get; internal set; }
    public DateTimeOffset? LastCompleteAt { get; internal set; }
    public long? LastAttemptGeneration { get; internal set; }
    public InventoryOutcome? LastOutcome { get; internal set; }
    public InventorySnapshotStatus? LastStatus { get; internal set; }
    public InventoryRejectionReason? LastRejectionReason { get; internal set; }
}
