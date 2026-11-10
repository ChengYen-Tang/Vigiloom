namespace Vigiloom.Modules.AgentManagement.Domain;

public enum HostLogSourceAction { Created = 1, Updated = 2, Retired = 3 }

/// <summary>Allow-listed source change metadata committed in the source transaction.</summary>
public sealed class HostLogSourceAudit
{
    private HostLogSourceAudit() { }
    public HostLogSourceAudit(HostLogSource source, HostLogSourceAction action)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(action) || source.Version.Length != 8) throw new ArgumentException("Invalid source audit.");
        SourceId = source.Id; DeviceId = source.DeviceId; ActorId = source.UpdatedBy; OccurredAt = source.UpdatedAt;
        Action = action; Generation = source.Generation; Enabled = source.Enabled; Version = source.Version.ToArray();
    }

    public long RowId { get; private set; }
    public Guid SourceId { get; private set; }
    public Guid DeviceId { get; private set; }
    public Guid ActorId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public HostLogSourceAction Action { get; private set; }
    public Guid Generation { get; private set; }
    public bool Enabled { get; private set; }
    public byte[] Version { get; private set; } = [];
}
