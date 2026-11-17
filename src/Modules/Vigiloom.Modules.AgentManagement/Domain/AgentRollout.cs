using Vigiloom.Contracts.Agent;

namespace Vigiloom.Modules.AgentManagement.Domain;

/// <summary>A group update whose membership is fixed when the rollout is created.</summary>
public sealed class AgentRollout
{
    public Guid Id { get; set; }
    public long RowId { get; set; }
    public Guid GroupId { get; set; }
    public Guid PackageId { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int PilotSize { get; set; }
    public int BatchSize { get; set; }
    public AgentRolloutStatus Status { get; set; }
    public byte[] Version { get; set; } = [];
}

/// <summary>One enrolled Agent and its assignment revision when the rollout was created.</summary>
public sealed class AgentRolloutMember
{
    public Guid RolloutId { get; set; }
    public Guid AgentId { get; set; }
    public Guid DeviceId { get; set; }
    public required string DeviceName { get; set; }
    public long DeviceGeneration { get; set; }
    public int Batch { get; set; }
    public int Position { get; set; }
    public byte[]? ExpectedAssignmentVersion { get; set; }
    public Guid? UpdateId { get; set; }
    public AgentRolloutMemberStatus Status { get; set; }
    public AgentRolloutProblem? Problem { get; set; }
}

public sealed class AgentRolloutAudit
{
    public long RowId { get; set; }
    public Guid RolloutId { get; set; }
    public Guid? ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public AgentRolloutAction Action { get; set; }
}
