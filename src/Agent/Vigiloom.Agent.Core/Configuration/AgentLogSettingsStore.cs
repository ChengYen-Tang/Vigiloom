using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Configuration;

/// <summary>Persists the last valid source snapshot, fenced by Agent identity and compare-and-swap.</summary>
public sealed class AgentLogSettingsStore(IdentityVault vault, Uri server, Guid agentId)
{
    private readonly Uri expected = IdentityClient.NormalizeServer(server);

    public async Task<AgentSavedLogSettings?> ReadAsync(CancellationToken token)
    {
        await using var session = await vault.AcquireForTransportAsync(token);
        return await ReadLockedAsync(session, token);
    }

    public async Task<AgentSavedLogSettings> SaveAsync(AgentLogSettingsSnapshot snapshot, Guid? expectedSaveToken,
        DateTimeOffset savedAt, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsValid()) throw new InvalidDataException("The log source settings are invalid or unsupported.");
        await using var session = await vault.AcquireForTransportAsync(token);
        var previous = await ReadLockedAsync(session, token);
        if (previous?.SaveToken != expectedSaveToken) throw new AgentSettingsConflictException();
        if (previous is not null && !snapshot.CanFollow(previous.Snapshot))
            throw new InvalidDataException("The log source identity or generation changed inconsistently.");
        var saved = new AgentSavedLogSettings(snapshot, savedAt.ToUniversalTime(), Guid.CreateVersion7(), agentId, expected.AbsoluteUri);
        await session.WriteLogSettingsAsync(saved, token);
        return saved;
    }

    private async Task<AgentSavedLogSettings?> ReadLockedAsync(IdentityVault.Session session, CancellationToken token)
    {
        var identity = await session.ReadAsync(token);
        if (agentId.Version != 7 || identity?.AgentId != agentId || !string.Equals(identity.Server, expected.AbsoluteUri, StringComparison.Ordinal))
            throw new InvalidOperationException("The registered Agent identity or server does not match the log settings owner.");
        var saved = await session.ReadLogSettingsAsync(token);
        if (saved is not null && (saved.AgentId != agentId || !string.Equals(saved.Server, expected.AbsoluteUri, StringComparison.Ordinal) ||
            saved.SaveToken.Version != 7 || saved.Snapshot is null || !saved.Snapshot.IsValid()))
            throw new InvalidDataException("The saved Agent log settings are invalid or unsupported.");
        return saved;
    }
}
