namespace Vigiloom.Modules.Resources.Domain;

/// <summary>A one-time credential for enrolling an Agent on a device.</summary>
public sealed class AgentEnrollment
{
    public Guid Id { get; set; }
    public long RowId { get; set; }
    public Guid DeviceId { get; set; }
    public long DeviceGeneration { get; set; }
    public byte[] Verifier { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public Guid? AgentId { get; set; }
}
