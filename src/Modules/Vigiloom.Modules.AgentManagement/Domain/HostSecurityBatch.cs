namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>Payload retained until the security service confirms processing.</summary>
public sealed class HostSecurityBatch
{
    public long RowId { get; private set; }
    public Guid AgentId { get; set; }
    public Guid BatchId { get; set; }
    public Guid DeviceId { get; set; }
    public long DeviceGeneration { get; set; }
    public string Kind { get; set; } = "";
    public byte[] Payload { get; set; } = [];
    public DateTimeOffset ReceivedAt { get; set; }
}
