namespace Vigiloom.Modules.Resources.Domain;

/// <summary>Agent identity and credential state owned by the resource service.</summary>
public sealed class RegisteredAgent
{
    public Guid Id { get; set; }
    public long RowId { get; set; }
    public Guid DeviceId { get; set; }
    public long DeviceGeneration { get; set; }
    public byte[] Verifier { get; set; } = [];
    public byte[]? PendingVerifier { get; set; }
    public DateTimeOffset? PendingUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}
