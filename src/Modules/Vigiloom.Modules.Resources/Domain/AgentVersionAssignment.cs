namespace Vigiloom.Modules.Resources.Domain;

/// <summary>The desired package for an Agent.</summary>
public sealed class AgentVersionAssignment
{
    public Guid AgentId { get; set; }
    public Guid? PackageId { get; set; }
    public Guid? UpdateId { get; set; }
    public Guid AssignedBy { get; set; }
    public DateTimeOffset AssignedAt { get; set; }
    public byte[] Version { get; set; } = [];
}
