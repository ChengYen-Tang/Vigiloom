using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.Resources.Domain;

/// <summary>An explicitly attested deployment relationship; it does not grant access or publication rights.</summary>
public sealed class DeploymentLink
{
    private DeploymentLink() { }

    public DeploymentLink(Guid deviceId, Guid instanceId, Guid actorId, string evidence, DateTimeOffset now)
    {
        if (deviceId.Version != 7 || instanceId.Version != 7 || actorId.Version != 7)
            throw new ArgumentException("Valid resource and administrator identifiers are required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
        if (evidence.Trim().Length > 512) throw new ArgumentException("Evidence is too long.", nameof(evidence));
        Id = ResourceIds.Create();
        DeviceId = deviceId;
        ServiceInstanceId = instanceId;
        AttestedBy = actorId;
        Evidence = evidence.Trim();
        StartedAt = now.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public long RowId { get; private set; }
    public Guid DeviceId { get; private set; }
    public Guid ServiceInstanceId { get; private set; }
    public Guid AttestedBy { get; private set; }
    public string Evidence { get; private set; } = "";
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? EndedAt { get; private set; }
    public byte[] Version { get; private set; } = [];

    public void End(DateTimeOffset now)
    {
        if (EndedAt is not null) throw new InvalidOperationException("The relationship has already ended.");
        var end = now.ToUniversalTime();
        if (end < StartedAt) throw new ArgumentOutOfRangeException(nameof(now));
        EndedAt = end;
    }
}
