using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Configuration;

/// <summary>Persists the last-good metric policy with an identity and compare-and-swap fence.</summary>
public sealed class AgentMetricSettingsStore(IdentityVault vault, Uri server, Guid agentId)
{
    private readonly Uri expected = IdentityClient.NormalizeServer(server);

    public async Task<AgentSavedMetricSettings?> ReadAsync(CancellationToken token)
    {
        await using var session = await vault.AcquireForTransportAsync(token);
        return await ReadLockedAsync(session, agentId, expected.AbsoluteUri, token);
    }

    public async Task<AgentSavedMetricSettings> SaveAsync(AgentMetricSettingsSnapshot snapshot, Guid? expectedSaveToken,
        DateTimeOffset savedAt, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsValid()) throw new InvalidDataException("The metric settings are invalid or unsupported.");
        await using var session = await vault.AcquireForTransportAsync(token);
        var previous = await ReadLockedAsync(session, agentId, expected.AbsoluteUri, token);
        if (previous?.SaveToken != expectedSaveToken) throw new AgentSettingsConflictException();
        var saved = new AgentSavedMetricSettings(snapshot, savedAt.ToUniversalTime(), Guid.CreateVersion7(), agentId, expected.AbsoluteUri);
        await session.WriteMetricSettingsAsync(saved, token);
        return saved;
    }

    internal static async Task<AgentSavedMetricSettings?> ReadLockedAsync(IdentityVault.Session session, Guid agentId, string server, CancellationToken token)
    {
        var identity = await session.ReadAsync(token);
        if (agentId.Version != 7 || identity?.AgentId != agentId || !string.Equals(identity.Server, server, StringComparison.Ordinal))
            throw new InvalidOperationException("The registered Agent identity or server does not match the metric settings owner.");
        var saved = await session.ReadMetricSettingsAsync(token);
        if (saved is not null && (saved.AgentId != agentId || !string.Equals(saved.Server, server, StringComparison.Ordinal) ||
            saved.SaveToken.Version != 7 || saved.Snapshot is null || !saved.Snapshot.IsValid()))
            throw new InvalidDataException("The saved Agent metric settings are invalid or unsupported.");
        return saved;
    }
}
