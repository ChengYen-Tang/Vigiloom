using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Hosting.Metrics;

/// <summary>Identity-bound last-good metric policy; saving does not imply that sampling applied it.</summary>
internal sealed class AgentMetricSettingsRuntime
{
    private State state = new(null, null, null);
    public AgentMetricSettings Current => Volatile.Read(ref state).Saved?.Snapshot.Settings ?? AgentMetricSettings.Default;

    public AgentMetricSettings? ForOwner(AgentSettingsOwner owner)
    {
        var current = Volatile.Read(ref state);
        return current.Owner == owner ? current.Saved?.Snapshot.Settings ?? AgentMetricSettings.Default : null;
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

    public void Publish(AgentSettingsOwner? owner, AgentSavedMetricSettings? saved)
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
            if (ReferenceEquals(Interlocked.CompareExchange(ref state, new(owner, current.Saved, failure), current), current)) return;
        }
    }

    private sealed record State(AgentSettingsOwner? Owner, AgentSavedMetricSettings? Saved, AgentConfigurationFailure? Failure);
}
