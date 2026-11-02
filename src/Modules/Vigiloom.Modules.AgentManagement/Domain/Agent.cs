using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.AgentManagement.Domain;

public sealed class Agent
{
    private Agent() { }

    public Agent(Guid deviceId, long generation, byte[] verifier, DateTimeOffset now)
    {
        Id = ResourceIds.Create();
        DeviceId = deviceId;
        DeviceGeneration = generation;
        Verifier = verifier;
        CreatedAt = now.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public long RowId { get; private set; }
    public Guid DeviceId { get; private set; }
    public long DeviceGeneration { get; private set; }
    public byte[] Verifier { get; private set; } = [];
    public byte[]? PendingVerifier { get; private set; }
    public DateTimeOffset? PendingUntil { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now.ToUniversalTime();
    public void BeginRotation(byte[] verifier, DateTimeOffset until) { PendingVerifier = verifier; PendingUntil = until.ToUniversalTime(); }
    public void FinishRotation() { Verifier = PendingVerifier!; PendingVerifier = null; PendingUntil = null; }
}
