namespace Vigiloom.Contracts.Agent;

/// <summary>Agent package metadata stored by the central platform.</summary>
public sealed record AgentPackageView(Guid Id, string Version, string RuntimeIdentifier, long Size,
    string Sha256)
{
    public bool Cached { get; init; }
}

/// <summary>One page of known Agent package metadata.</summary>
public sealed record AgentPackagePage(IReadOnlyList<AgentPackageView> Items, int Page, bool HasMore);
