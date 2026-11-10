using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Hosting.Logs;

/// <summary>Identity-bound last-good desired settings. Downloaded settings are not applied status.</summary>
internal sealed class AgentLogSettingsRuntime
{
    private State state = new(null, null, null);

    public AgentLogSettingsSnapshot? ForOwner(AgentSettingsOwner owner)
        => SavedForOwner(owner)?.Snapshot;

    public AgentSavedLogSettings? SavedForOwner(AgentSettingsOwner owner)
    {
        var current = Volatile.Read(ref state);
        return current.Owner == owner && current.Failure != AgentConfigurationFailure.Unauthorized ? current.Saved : null;
    }

    public void Bind(AgentSettingsOwner? owner)
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (current.Owner == owner) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref state, new(owner, null, null), current), current)) return;
        }
    }

    public void Publish(AgentSettingsOwner? owner, AgentSavedLogSettings? saved)
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (current.Owner != owner || (saved is not null && (owner is null || saved.AgentId != owner.AgentId ||
                !string.Equals(saved.Server, owner.Server, StringComparison.Ordinal)))) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref state, new(owner, saved, null), current), current)) return;
        }
    }

    public void MarkFailure(AgentSettingsOwner? owner, AgentConfigurationFailure failure)
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (current.Owner != owner) return;
            var effective = current.Failure == AgentConfigurationFailure.Unauthorized ? current.Failure : failure;
            if (ReferenceEquals(Interlocked.CompareExchange(ref state, new(owner, current.Saved, effective), current), current)) return;
        }
    }

    private sealed record State(AgentSettingsOwner? Owner, AgentSavedLogSettings? Saved, AgentConfigurationFailure? Failure);
}
