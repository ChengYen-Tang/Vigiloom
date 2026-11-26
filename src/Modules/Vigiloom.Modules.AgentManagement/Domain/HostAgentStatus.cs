namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>Host-owned heartbeat and reporting state; identity remains in Resources.</summary>
public sealed class HostAgentStatus : AgentStatus
{
    private HostAgentStatus() { }

    public HostAgentStatus(Guid agentId, Guid deviceId, long deviceGeneration, DateTimeOffset createdAt)
    {
        Id = agentId;
        DeviceId = deviceId;
        DeviceGeneration = deviceGeneration;
        CreatedAt = createdAt.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public long RowId { get; private set; }
    public Guid DeviceId { get; private set; }
    public long DeviceGeneration { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
}
