using Vigiloom.BuildingBlocks.Identifiers;

namespace Vigiloom.Modules.Resources.Domain;

/// <summary>Identity metadata for a service awaiting approval; no telemetry payload is stored.</summary>
public sealed class ApplicationCandidate
{
    private ApplicationCandidate() { }

    public ApplicationCandidate(Guid sourceId, string serviceKey, string environment, string displayName, string serviceNamespace, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(sourceId, Guid.Empty);
        Id = ResourceIds.Create();
        SourceId = sourceId;
        ServiceKey = serviceKey;
        ServiceNamespace = serviceNamespace;
        Environment = environment;
        DisplayName = displayName;
        FirstSeen = LastSeen = CreatedAt = UpdatedAt = now.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public long RowId { get; private set; }
    public Guid SourceId { get; private set; }
    public string ServiceKey { get; private set; } = "";
    public string ServiceNamespace { get; private set; } = "";
    public byte[] ServiceNamespaceBytes { get; private set; } = [];
    public string Environment { get; private set; } = "";
    public string DisplayName { get; private set; } = "";
    public DateTimeOffset FirstSeen { get; private set; }
    public DateTimeOffset LastSeen { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
}
