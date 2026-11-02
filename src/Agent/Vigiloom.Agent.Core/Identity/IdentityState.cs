namespace Vigiloom.Agent.Core.Identity;

/// <summary>Private durable Agent enrollment and rotation state. Never log or expose this object.</summary>
public sealed class IdentityState
{
    public int Version { get; set; } = 1;
    public string Server { get; set; } = "";
    public Guid? AgentId { get; set; }
    public string? EnrollmentSecret { get; set; }
    public string MachineSecret { get; set; } = "";
    public string? PendingSecret { get; set; }
}
