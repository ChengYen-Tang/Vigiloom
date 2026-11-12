using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Core.Configuration;

/// <summary>Persists the last-good inventory policy with an identity and compare-and-swap fence.</summary>
public sealed class AgentInventorySettingsStore(IdentityVault vault, Uri server, Guid agentId)
{
    private readonly Uri expected = IdentityClient.NormalizeServer(server);

    public async Task<AgentSavedInventorySettings?> ReadAsync(CancellationToken token)
    {
        await using var session = await vault.AcquireForTransportAsync(token);
        return await ReadLockedAsync(session, agentId, expected.AbsoluteUri, token);
    }

    public async Task<AgentSavedInventorySettings> SaveAsync(InventorySettingsSnapshot snapshot, Guid? expectedSaveToken,
        DateTimeOffset savedAt, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsValid()) throw new InvalidDataException("The inventory settings are invalid or unsupported.");
        await using var session = await vault.AcquireForTransportAsync(token);
        var previous = await ReadLockedAsync(session, agentId, expected.AbsoluteUri, token);
        if (previous?.SaveToken != expectedSaveToken) throw new AgentSettingsConflictException();
        var saved = new AgentSavedInventorySettings(snapshot, savedAt.ToUniversalTime(), Guid.CreateVersion7(), agentId, expected.AbsoluteUri);
        // Write the extended policy first and commit its matching token by replacing the
        // schema-1 mirror last. Older Agents can continue reading that mirror after rollback.
        await session.WriteExtendedInventorySettingsAsync(saved, token);
        var legacy = saved with { Snapshot = InventorySettingsSnapshot.Create(new(snapshot.Settings.ScanIntervalSeconds)) };
        await session.WriteInventorySettingsAsync(legacy, token);
        return saved;
    }

    private static async Task<AgentSavedInventorySettings?> ReadLockedAsync(IdentityVault.Session session, Guid agentId, string server, CancellationToken token)
    {
        var identity = await session.ReadAsync(token);
        if (agentId.Version != 7 || identity?.AgentId != agentId || !string.Equals(identity.Server, server, StringComparison.Ordinal))
            throw new InvalidOperationException("The registered Agent identity or server does not match the inventory settings owner.");
        var saved = await session.ReadInventorySettingsAsync(token);
        // A re-enrolled identity must neither apply nor be blocked by its predecessor's policy.
        if (saved is not null && (saved.AgentId != agentId || !string.Equals(saved.Server, server, StringComparison.Ordinal))) return null;
        if (saved is not null && (saved.SaveToken.Version != 7 || saved.Snapshot is null || !saved.Snapshot.IsValid()))
            throw new InvalidDataException("The saved Agent inventory settings are invalid or unsupported.");
        if (saved is not null)
        {
            var extended = await session.ReadExtendedInventorySettingsAsync(token);
            // A crash between writes, or an older Agent saving the mirror, invalidates the
            // extended copy. Fall back to the legacy policy, which never enables path collection.
            if (extended is not null && extended.SaveToken == saved.SaveToken && extended.AgentId == saved.AgentId &&
                string.Equals(extended.Server, saved.Server, StringComparison.Ordinal) && extended.Snapshot is not null &&
                extended.Snapshot.IsValid() && extended.Snapshot.Settings.ScanIntervalSeconds == saved.Snapshot.Settings.ScanIntervalSeconds)
                return extended;
        }
        return saved;
    }
}
