namespace Vigiloom.Contracts.Agent;

public enum AgentRolloutStatus { Running = 1, Paused = 2, Completed = 3, Cancelled = 4, CompletedWithIssues = 5 }
public enum AgentRolloutMemberStatus { Pending = 1, Assigned = 2, Succeeded = 3, Failed = 4, Conflict = 5 }
public enum AgentRolloutProblem { AssignmentChanged = 1, AgentUnavailable = 2, PackageUnavailable = 3, UpdateFailed = 4 }
public enum AgentRolloutAction { Created = 1, Paused = 2, Resumed = 3, Cancelled = 4, AutomaticallyPaused = 5, ResumeRemaining = 6 }
public enum AgentRolloutResult { Saved, NotFound, Invalid, Conflict }
public static class AgentRolloutLimits { public const int MaximumOutstanding = 100; }

public sealed record AgentRolloutInput(Guid GroupId, Guid PackageId, int PilotSize, int BatchSize);
public sealed record AgentRolloutChange(string Revision, AgentRolloutAction Action);
public sealed record AgentRolloutCreated(Guid Id, int Included, int Excluded);
public sealed record AgentRolloutCreation(AgentRolloutResult Result, Guid? Id = null, int Included = 0, int Excluded = 0);
public sealed record AgentRolloutPreview(int Included, int Excluded, string RuntimeIdentifier);
public sealed record AgentRolloutSummary(Guid Id, Guid PackageId, string PackageVersion, string RuntimeIdentifier, DateTimeOffset CreatedAt, AgentRolloutStatus Status);
public sealed record AgentRolloutMemberView(Guid AgentId, Guid DeviceId, string DeviceName, int Batch, int Position, Guid? UpdateId,
    AgentRolloutMemberStatus Status, AgentRolloutProblem? Problem);
public sealed record AgentRolloutAuditView(Guid? ActorId, DateTimeOffset OccurredAt, AgentRolloutAction Action);
public sealed record AgentRolloutView(Guid Id, Guid GroupId, Guid PackageId, string PackageVersion, string RuntimeIdentifier, DateTimeOffset CreatedAt,
    int PilotSize, int BatchSize, AgentRolloutStatus Status, string Revision, IReadOnlyList<AgentRolloutMemberView> Members,
    IReadOnlyList<AgentRolloutAuditView> History);

public interface IAgentRolloutClient
{
    Task<IReadOnlyList<AgentRolloutSummary>> ListAsync(Guid groupId, CancellationToken token);
    Task<AgentRolloutView?> GetAsync(Guid id, CancellationToken token);
    Task<AgentRolloutPreview?> PreviewAsync(Guid groupId, Guid packageId, CancellationToken token);
    Task<AgentRolloutCreation> CreateAsync(AgentRolloutInput input, CancellationToken token);
    Task<AgentRolloutResult> ChangeAsync(Guid id, AgentRolloutChange input, CancellationToken token);
}
