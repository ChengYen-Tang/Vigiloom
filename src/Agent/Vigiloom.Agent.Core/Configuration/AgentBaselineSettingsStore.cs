using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent.Baseline;

namespace Vigiloom.Agent.Core.Configuration;

/// <summary>Stores the last valid baseline policy under the current Agent identity.</summary>
public sealed class AgentBaselineSettingsStore(IdentityVault vault, Uri server, Guid agentId)
{
    private readonly Uri expected = IdentityClient.NormalizeServer(server);

    public async Task<AgentSavedBaselineSettings?> ReadAsync(CancellationToken token)
    {
        await using var session = await vault.AcquireForTransportAsync(token);
        return await ReadLockedAsync(session, token);
    }

    public async Task<AgentSavedBaselineSettings> SaveAsync(BaselineSettingsSnapshot snapshot, Guid? expectedSaveToken,
        DateTimeOffset savedAt, CancellationToken token)
    {
        if (!snapshot.IsValid()) throw new InvalidDataException("The baseline settings are invalid or unsupported.");
        await using var session = await vault.AcquireForTransportAsync(token);
        var previous = await ReadLockedAsync(session, token);
        if (previous?.SaveToken != expectedSaveToken) throw new AgentSettingsConflictException();
        var saved = new AgentSavedBaselineSettings(snapshot, savedAt.ToUniversalTime(), Guid.CreateVersion7(), agentId, expected.AbsoluteUri);
        await session.WriteBaselineSettingsAsync(saved, token);
        return saved;
    }

    public async Task<AgentSavedBaselineSettings> ConfirmAsync(Guid expectedSaveToken, CancellationToken token)
    {
        await using var session = await vault.AcquireForTransportAsync(token);
        var current = await ReadLockedAsync(session, token);
        if (current is null || current.SaveToken != expectedSaveToken) throw new AgentSettingsConflictException();
        return current;
    }

    private async Task<AgentSavedBaselineSettings?> ReadLockedAsync(IdentityVault.Session session, CancellationToken token)
    {
        var identity = await session.ReadAsync(token);
        if (agentId.Version != 7 || identity?.AgentId != agentId || !string.Equals(identity.Server, expected.AbsoluteUri, StringComparison.Ordinal))
            throw new InvalidOperationException("The registered Agent identity or server does not match the baseline settings owner.");
        var saved = await session.ReadBaselineSettingsAsync(token);
        if (saved is not null && (saved.AgentId != agentId || !string.Equals(saved.Server, expected.AbsoluteUri, StringComparison.Ordinal))) return null;
        if (saved is not null && (saved.SaveToken.Version != 7 || saved.Snapshot is null || !saved.Snapshot.IsValid()))
            throw new InvalidDataException("The saved baseline settings are invalid or unsupported.");
        return saved;
    }
}
