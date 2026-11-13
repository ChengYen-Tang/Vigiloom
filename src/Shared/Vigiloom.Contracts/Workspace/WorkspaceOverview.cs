namespace Vigiloom.Contracts.Workspace;

/// <summary>One monitoring observation; its time is specific to its source.</summary>
public sealed record WorkspaceItem(Guid Id, string Name, string Detail, string State, DateTimeOffset? ObservedAt, int Priority);

public sealed record WorkspaceSection(int Total, int Attention, IReadOnlyList<WorkspaceItem> Items, bool Unavailable = false);
public sealed record WorkspaceGroup(Guid Id, string Name);
public sealed record WorkspaceOverview(WorkspaceSection Ping, WorkspaceSection Agents, WorkspaceSection Services,
    IReadOnlyList<WorkspaceGroup> Groups, bool GroupsUnavailable, DateTimeOffset GeneratedAt);

public interface IWorkspaceOverviewClient
{
    Task<WorkspaceOverview?> GetAsync(CancellationToken cancellationToken = default);
}
