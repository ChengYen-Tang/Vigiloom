namespace Vigiloom.Contracts.Agent;

/// <summary>Authenticated Agent package metadata, including current catalogue availability.</summary>
/// <remarks>Listed alone does not authorize download: revocation, expiry, platform and assignment must also be checked.</remarks>
public sealed record AgentPackageView(Guid Id, string Version, string RuntimeIdentifier, long Size,
    string Sha256, bool Listed, bool Revoked, DateTimeOffset VerifiedUntil);

/// <summary>One page of known Agent package metadata.</summary>
public sealed record AgentPackagePage(IReadOnlyList<AgentPackageView> Items, int Page, bool HasMore);
