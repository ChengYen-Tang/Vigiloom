namespace Vigiloom.Contracts.Workspace;

/// <summary>Non-secret identity information for the current authenticated monitoring user.</summary>
public sealed record WorkspaceIdentity(string UserName, IReadOnlyList<string> Roles);

/// <summary>Retrieves the current, server-authorized workspace identity.</summary>
public interface IWorkspaceIdentityClient
{
    /// <returns>The current identity, or null when the session is no longer authorized.</returns>
    Task<WorkspaceIdentity?> GetAsync(CancellationToken cancellationToken = default);
}
