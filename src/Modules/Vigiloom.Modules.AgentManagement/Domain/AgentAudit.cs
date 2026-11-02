namespace Vigiloom.Modules.AgentManagement.Domain;

public sealed class AgentAudit
{
    private AgentAudit() { }

    public AgentAudit(Guid deviceId, Guid? agentId, Guid? actorId, string action, DateTimeOffset occurredAt)
    {
        DeviceId = deviceId;
        AgentId = agentId;
        ActorId = actorId;
        Action = action;
        OccurredAt = occurredAt.ToUniversalTime();
    }

    public long RowId { get; private set; }
    public Guid DeviceId { get; private set; }
    public Guid? AgentId { get; private set; }
    public Guid? ActorId { get; private set; }
    public string Action { get; private set; } = null!;
    public DateTimeOffset OccurredAt { get; private set; }
}
