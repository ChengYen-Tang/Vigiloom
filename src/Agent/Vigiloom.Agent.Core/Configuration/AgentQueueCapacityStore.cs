using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Configuration;

/// <summary>Stores one identity-bound last-good queue-capacity group with a cross-process CAS fence.</summary>
public sealed class AgentQueueCapacityStore(IdentityVault vault, Uri server, Guid agentId)
{
    private readonly Uri expected = IdentityClient.NormalizeServer(server);

    public async Task<AgentAppliedQueueCapacity?> ReadAsync(CancellationToken token)
    {
        await using var session = await vault.AcquireForTransportAsync(token);
        return await ReadLockedAsync(session, agentId, expected.AbsoluteUri, token);
    }

    public async Task<AgentAppliedQueueCapacity> SaveAsync(AgentQueueCapacitySnapshot snapshot, Guid? expectedSaveToken, DateTimeOffset appliedAt, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsValid()) throw new InvalidDataException("The Agent queue capacity is invalid or unsupported.");
        await using var session = await vault.AcquireForTransportAsync(token);
        var previous = await ReadLockedAsync(session, agentId, expected.AbsoluteUri, token);
        if (previous?.SaveToken != expectedSaveToken) throw new AgentSettingsConflictException();
        var applied = new AgentAppliedQueueCapacity(snapshot, appliedAt.ToUniversalTime(), Guid.CreateVersion7(), agentId, expected.AbsoluteUri);
        await session.WriteQueueCapacityAsync(applied, token);
        return applied;
    }

    // The caller already owns the identity lease; no HTTP request is made while it is held.
    internal static async Task<AgentAppliedQueueCapacity?> ReadLockedAsync(IdentityVault.Session session, Guid agentId, string expectedServer, CancellationToken token)
    {
        var identity = await session.ReadAsync(token);
        if (agentId.Version != 7 || identity?.AgentId != agentId || !string.Equals(identity.Server, expectedServer, StringComparison.Ordinal))
            throw new InvalidOperationException("The registered Agent identity or server does not match the capacity owner.");
        var applied = await session.ReadQueueCapacityAsync(token);
        if (applied is not null && (applied.AgentId != agentId || !string.Equals(applied.Server, expectedServer, StringComparison.Ordinal) ||
            applied.SaveToken.Version != 7 || applied.Snapshot is null || !applied.Snapshot.IsValid()))
            throw new InvalidDataException("The saved Agent queue capacity is invalid or unsupported.");
        return applied;
    }

    internal static async Task<AgentQueueCapacity> ReadEffectiveLockedAsync(IdentityVault.Session session, Guid agentId, CancellationToken token)
    {
        var applied = await session.ReadQueueCapacityAsync(token);
        if (applied is null) return AgentQueueCapacity.Default;
        var identity = await session.ReadAsync(token);
        if (agentId.Version != 7 || identity?.AgentId != agentId || applied.AgentId != agentId ||
            !string.Equals(applied.Server, identity.Server, StringComparison.Ordinal) || applied.SaveToken.Version != 7 ||
            applied.Snapshot is null || !applied.Snapshot.IsValid())
            throw new InvalidDataException("The saved Agent queue capacity does not match the current identity or is invalid.");
        return applied.Snapshot.Capacity;
    }
}
