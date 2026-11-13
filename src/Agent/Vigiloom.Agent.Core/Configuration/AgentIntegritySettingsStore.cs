using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Agent.Core.Configuration;

/// <summary>Persists the last valid integrity policy under its Agent identity and server.</summary>
public sealed class AgentIntegritySettingsStore(IdentityVault vault, Uri server, Guid agentId)
{
    private readonly Uri expected = IdentityClient.NormalizeServer(server);

    public async Task<AgentSavedIntegritySettings?> ReadAsync(CancellationToken token)
    {
        await using var session = await vault.AcquireForTransportAsync(token);
        return await ReadLockedAsync(session, token);
    }

    public async Task<AgentSavedIntegritySettings> SaveAsync(IntegritySettingsSnapshot snapshot, Guid? expectedSaveToken,
        DateTimeOffset savedAt, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsValid()) throw new InvalidDataException("The integrity settings are invalid or unsupported.");
        await using var session = await vault.AcquireForTransportAsync(token);
        var previous = await ReadLockedAsync(session, token);
        if (previous?.SaveToken != expectedSaveToken) throw new AgentSettingsConflictException();
        if (previous is not null && !snapshot.CanFollow(previous.Snapshot))
            throw new InvalidDataException("The integrity scope revision changed inconsistently.");
        var saved = new AgentSavedIntegritySettings(snapshot, savedAt.ToUniversalTime(), Guid.CreateVersion7(), agentId, expected.AbsoluteUri);
        await session.WriteIntegritySettingsAsync(saved, token);
        return saved;
    }

    /// <summary>Rechecks owner and token after an HTTP response without changing an unchanged policy.</summary>
    public async Task<AgentSavedIntegritySettings> ConfirmAsync(Guid expectedSaveToken, CancellationToken token)
    {
        await using var session = await vault.AcquireForTransportAsync(token);
        var current = await ReadLockedAsync(session, token);
        if (current is null || current.SaveToken != expectedSaveToken) throw new AgentSettingsConflictException();
        return current;
    }

    private async Task<AgentSavedIntegritySettings?> ReadLockedAsync(IdentityVault.Session session, CancellationToken token)
    {
        var identity = await session.ReadAsync(token);
        if (agentId.Version != 7 || identity?.AgentId != agentId || !string.Equals(identity.Server, expected.AbsoluteUri, StringComparison.Ordinal))
            throw new InvalidOperationException("The registered Agent identity or server does not match the integrity settings owner.");
        var saved = await session.ReadIntegritySettingsAsync(token);
        if (saved is not null && (saved.AgentId != agentId || !string.Equals(saved.Server, expected.AbsoluteUri, StringComparison.Ordinal))) return null;
        if (saved is not null && (saved.SaveToken.Version != 7 || saved.Snapshot is null || !saved.Snapshot.IsValid()))
            throw new InvalidDataException("The saved integrity settings are invalid or unsupported.");
        return saved;
    }
}
