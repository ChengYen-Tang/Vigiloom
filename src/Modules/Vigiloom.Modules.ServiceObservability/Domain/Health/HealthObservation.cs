namespace Vigiloom.Modules.ServiceObservability.Domain.Health;

public enum HealthObservationKind : byte
{
    Healthy = 1, Degraded = 2, Unhealthy = 3, Timeout = 4, TransportError = 5, InvalidResponse = 6, NetworkDenied = 7
}

/// <summary>A recorded probe result, without endpoint, credentials, response body or exception text.</summary>
public sealed class HealthObservation
{
    private HealthObservation() { }

    internal HealthObservation(Guid targetId, long leaseGeneration, long networkGeneration,
        byte[] sourceVersion, byte[] serviceVersion, byte[]? instanceVersion, byte[] targetVersion, byte[] networkVersion,
        HealthObservationKind kind, DateTimeOffset observedAt, DateTimeOffset receivedAt)
    {
        if (targetId.Version != 7 || leaseGeneration < 1 || networkGeneration < 1 ||
            kind is < HealthObservationKind.Healthy or > HealthObservationKind.NetworkDenied ||
            sourceVersion.Length != 8 || serviceVersion.Length != 8 || targetVersion.Length != 8 || networkVersion.Length != 8 ||
            (instanceVersion is not null && instanceVersion.Length != 8))
            throw new ArgumentException("Invalid health observation.");
        TargetId = targetId;
        LeaseGeneration = leaseGeneration;
        NetworkGeneration = networkGeneration;
        SourceVersion = sourceVersion;
        ServiceVersion = serviceVersion;
        InstanceVersion = instanceVersion;
        TargetVersion = targetVersion;
        NetworkVersion = networkVersion;
        Kind = kind;
        ObservedAt = observedAt.ToUniversalTime();
        ReceivedAt = receivedAt.ToUniversalTime();
    }

    public long RowId { get; private set; }
    public Guid TargetId { get; private set; }
    public long LeaseGeneration { get; private set; }
    public long NetworkGeneration { get; private set; }
    public byte[] SourceVersion { get; private set; } = [];
    public byte[] ServiceVersion { get; private set; } = [];
    public byte[]? InstanceVersion { get; private set; }
    public byte[] TargetVersion { get; private set; } = [];
    public byte[] NetworkVersion { get; private set; } = [];
    public HealthObservationKind Kind { get; private set; }
    public DateTimeOffset ObservedAt { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
}
