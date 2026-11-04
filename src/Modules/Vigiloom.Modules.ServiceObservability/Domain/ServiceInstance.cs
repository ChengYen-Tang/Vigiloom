namespace Vigiloom.Modules.ServiceObservability.Domain;

/// <summary>A centrally assigned identity for one trusted external service instance.</summary>
public sealed class ServiceInstance
{
    private ServiceInstance() { }

    public Guid Id { get; private set; }
    public long RowId { get; private set; }
    public Guid ServiceId { get; private set; }
    public Guid SourceScopeId { get; private set; }
    public string ExternalInstanceId { get; private set; } = "";
    public byte[] ExternalInstanceIdBytes { get; private set; } = [];
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? RetiredAt { get; private set; }
    public byte[] Version { get; private set; } = [];
}
