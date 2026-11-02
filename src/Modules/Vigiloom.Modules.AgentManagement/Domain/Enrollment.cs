using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.AgentManagement.Domain;

public sealed class Enrollment
{
    private Enrollment() { }

    public Enrollment(Guid deviceId, long generation, byte[] verifier, DateTimeOffset now, DateTimeOffset expiresAt)
    {
        Id = ResourceIds.Create();
        DeviceId = deviceId;
        DeviceGeneration = generation;
        Verifier = verifier;
        CreatedAt = now.ToUniversalTime();
        ExpiresAt = expiresAt.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public long RowId { get; private set; }
    public Guid DeviceId { get; private set; }
    public long DeviceGeneration { get; private set; }
    public byte[] Verifier { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public Guid? AgentId { get; private set; }

    public void Consume(Guid agentId) => AgentId = agentId;
    public void Revoke(DateTimeOffset now) => RevokedAt ??= now.ToUniversalTime();
}
