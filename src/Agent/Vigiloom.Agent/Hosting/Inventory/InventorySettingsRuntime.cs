using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Agent.Hosting.Inventory;

/// <summary>Identity-bound last-good policy and synchronization failure.</summary>
internal sealed class InventorySettingsRuntime
{
    private State state = new(null, null, null, null, null);

    public InventorySettings? ForOwner(AgentSettingsOwner owner)
    {
        var current = Volatile.Read(ref state);
        return current.Owner == owner ? current.Saved?.Snapshot.Settings ?? InventorySettings.Default : null;
    }

    public InventorySettings? ApplyForOwner(AgentSettingsOwner owner, DateTimeOffset now)
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (current.Owner != owner) return null;
            var snapshot = current.Saved?.Snapshot;
            if (snapshot is null || current.Applied?.Revision == snapshot.Revision)
                return snapshot?.Settings ?? InventorySettings.Default;
            var next = current with { Applied = snapshot, AppliedAt = now.ToUniversalTime() };
            if (ReferenceEquals(Interlocked.CompareExchange(ref state, next, current), current)) return snapshot.Settings;
        }
    }

    public InventorySettingsObservation? Report(AgentSettingsOwner? owner)
    {
        var current = Volatile.Read(ref state);
        return owner is not null && current.Owner == owner
            ? new(owner, new(current.Applied, current.AppliedAt, current.Failure)) : null;
    }

    public void Bind(AgentSettingsOwner? owner)
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (current.Owner == owner) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref state, new(owner, null, null, null, null), current), current)) return;
        }
    }

    public bool NeedsRestore(AgentSettingsOwner owner)
    {
        var current = Volatile.Read(ref state);
        return current.Owner == owner && current.Saved is null;
    }

    // A delayed local read must neither replace a newer value nor clear a synchronization error.
    public void RestoreIfEmpty(AgentSettingsOwner? owner, AgentSavedInventorySettings saved)
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (owner is null || current.Owner != owner || current.Saved is not null || saved.AgentId != owner.AgentId ||
                !string.Equals(saved.Server, owner.Server, StringComparison.Ordinal)) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref state, current with { Saved = saved }, current), current)) return;
        }
    }

    public void Publish(AgentSettingsOwner? owner, AgentSavedInventorySettings saved)
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (current.Owner != owner || owner is null || saved.AgentId != owner.AgentId ||
                !string.Equals(saved.Server, owner.Server, StringComparison.Ordinal)) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref state, current with { Saved = saved, Failure = null }, current), current)) return;
        }
    }

    public void MarkFailure(AgentSettingsOwner? owner, AgentConfigurationFailure failure)
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (current.Owner != owner) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref state, current with { Failure = failure }, current), current)) return;
        }
    }

    private sealed record State(AgentSettingsOwner? Owner, AgentSavedInventorySettings? Saved, AgentConfigurationFailure? Failure,
        InventorySettingsSnapshot? Applied, DateTimeOffset? AppliedAt);
}
