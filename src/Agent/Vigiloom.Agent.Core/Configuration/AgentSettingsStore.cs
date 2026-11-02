using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Configuration;

/// <summary>Stores a validated last-good configuration with its protected Agent identity.</summary>
public sealed class AgentSettingsStore(IdentityVault vault, Uri server, Guid agentId)
{
    private readonly Uri expected = IdentityClient.NormalizeServer(server);

    /// <summary>Return the last valid saved settings, or null when no settings have been applied.</summary>
    public async Task<AgentAppliedSettings?> ReadAsync(CancellationToken token)
    {
        await using var session = await vault.AcquireForTransportAsync(token);
        await ReadIdentityAsync(session, token);
        var applied = await session.ReadSettingsAsync(token);
        if (applied is not null && !ValidSavedSettings(applied))
            throw new InvalidDataException("The saved Agent configuration is invalid or unsupported.");
        return applied;
    }

    /// <summary>Durably replace the last-good snapshot only after validating it.</summary>
    public async Task<AgentAppliedSettings> SaveAsync(AgentSettingsSnapshot snapshot, Guid? expectedSaveToken, DateTimeOffset appliedAt, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsValid()) throw new InvalidDataException("The Agent configuration is invalid or unsupported.");
        await using var session = await vault.AcquireForTransportAsync(token);
        await ReadIdentityAsync(session, token);
        var previous = await session.ReadSettingsAsync(token);
        if (previous is not null && !ValidSavedSettings(previous))
            throw new InvalidDataException("The saved Agent configuration is invalid or unsupported.");
        if (previous?.SaveToken != expectedSaveToken)
            throw new AgentSettingsConflictException();
        var applied = new AgentAppliedSettings(snapshot, appliedAt.ToUniversalTime(), Guid.CreateVersion7(), agentId, expected.AbsoluteUri);
        await session.WriteSettingsAsync(applied, token);
        return applied;
    }

    private async Task<IdentityState> ReadIdentityAsync(IdentityVault.Session session, CancellationToken token)
    {
        var identity = await session.ReadAsync(token);
        if (agentId.Version != 7 || identity?.AgentId != agentId || !string.Equals(identity.Server, expected.AbsoluteUri, StringComparison.Ordinal))
            throw new InvalidOperationException("The registered Agent identity or server does not match the settings owner.");
        return identity;
    }

    private bool ValidSavedSettings(AgentAppliedSettings settings) => settings.AgentId == agentId &&
        string.Equals(settings.Server, expected.AbsoluteUri, StringComparison.Ordinal) && settings.SaveToken.Version == 7 &&
        settings.Snapshot is not null && settings.Snapshot.IsValid();
}

/// <summary>A newer snapshot was saved while the requested configuration was in flight.</summary>
public sealed class AgentSettingsConflictException() : InvalidOperationException("The saved Agent configuration changed during synchronization.");
