namespace Vigiloom.Modules.AgentManagement.Domain;

public sealed class AgentAudit
{
    private AgentAudit() { }

    public AgentAudit(Guid deviceId, Guid? agentId, Guid? actorId, string action, DateTimeOffset occurredAt,
        Guid? previousPackageId = null, Guid? packageId = null)
    {
        DeviceId = deviceId;
        AgentId = agentId;
        ActorId = actorId;
        Action = action;
        OccurredAt = occurredAt.ToUniversalTime();
        PreviousPackageId = previousPackageId;
        PackageId = packageId;
    }

    public long RowId { get; private set; }
    public Guid DeviceId { get; private set; }
    public Guid? AgentId { get; private set; }
    public Guid? ActorId { get; private set; }
    public string Action { get; private set; } = null!;
    public DateTimeOffset OccurredAt { get; private set; }
    public Guid? PreviousPackageId { get; private set; }
    public Guid? PackageId { get; private set; }
}
