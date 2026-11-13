using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Hosting.Integrity;

/// <summary>Identity-bound last-good desired settings. Downloaded settings are not applied status.</summary>
internal sealed class AgentIntegritySettingsRuntime
{
    private State state = new(null, null, null, null, null);

    public IntegritySettingsSnapshot? ForOwner(AgentSettingsOwner owner)
        => SavedForOwner(owner)?.Snapshot;

    public AgentSavedIntegritySettings? SavedForOwner(AgentSettingsOwner owner)
    {
        var current = Volatile.Read(ref state);
        return current.Owner == owner && current.Failure != AgentConfigurationFailure.Unauthorized ? current.Saved : null;
    }

    public AgentSavedIntegritySettings? ApplyForOwner(AgentSettingsOwner owner, DateTimeOffset now)
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (current.Owner != owner || current.Failure == AgentConfigurationFailure.Unauthorized) return null;
            var saved = current.Saved;
            if (saved is null || current.Applied?.Revision == saved.Snapshot.Revision) return saved;
            var next = current with { Applied = saved.Snapshot, AppliedAt = now.ToUniversalTime() };
            if (ReferenceEquals(Interlocked.CompareExchange(ref state, next, current), current)) return saved;
        }
    }

    public (AgentSettingsOwner Owner, IntegritySettingsReport Report)? Report(AgentSettingsOwner? owner)
    {
        var current = Volatile.Read(ref state);
        return owner is not null && current.Owner == owner
            ? (owner, new(current.Applied?.Revision, current.Applied?.Generation, current.AppliedAt, current.Failure)) : null;
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
    public void RestoreIfEmpty(AgentSettingsOwner? owner, AgentSavedIntegritySettings saved)
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (owner is null || current.Owner != owner || current.Saved is not null || saved.AgentId != owner.AgentId ||
                !string.Equals(saved.Server, owner.Server, StringComparison.Ordinal)) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref state, current with { Saved = saved }, current), current)) return;
        }
    }

    public void Publish(AgentSettingsOwner? owner, AgentSavedIntegritySettings? saved)
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (current.Owner != owner || (saved is not null && (owner is null || saved.AgentId != owner.AgentId ||
                !string.Equals(saved.Server, owner.Server, StringComparison.Ordinal)))) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref state, current with { Saved = saved, Failure = null }, current), current)) return;
        }
    }

    public void MarkFailure(AgentSettingsOwner? owner, AgentConfigurationFailure failure)
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (current.Owner != owner) return;
            var effective = current.Failure == AgentConfigurationFailure.Unauthorized ? current.Failure : failure;
            if (ReferenceEquals(Interlocked.CompareExchange(ref state, current with { Failure = effective }, current), current)) return;
        }
    }

    private sealed record State(AgentSettingsOwner? Owner, AgentSavedIntegritySettings? Saved, AgentConfigurationFailure? Failure,
        IntegritySettingsSnapshot? Applied, DateTimeOffset? AppliedAt);
}
