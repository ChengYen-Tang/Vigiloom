using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Hosting;

/// <summary>Publishes one complete, immutable configuration to Agent workers.</summary>
internal sealed class AgentSettingsRuntime
{
    private State state = new(null, null, null);
    public AgentAppliedSettings? Applied => Volatile.Read(ref state).Applied;
    public AgentSettings Current => Applied?.Snapshot.Settings ?? AgentSettings.Default;
    public void Bind(AgentSettingsOwner? owner)
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (current.Owner == owner) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref state, new(owner, null, null), current), current)) return;
        }
    }
    public void Publish(AgentSettingsOwner? owner, AgentAppliedSettings? value)
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (current.Owner != owner) return;
            if (value is not null && (owner is null || value.AgentId != owner.AgentId ||
                !string.Equals(value.Server, owner.Server, StringComparison.Ordinal))) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref state, new(owner, value, null), current), current)) return;
        }
    }
    public void MarkFailure(AgentSettingsOwner? owner, AgentConfigurationFailure failure)
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (current.Owner != owner) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref state, new(owner, current.Applied, failure), current), current)) return;
        }
    }
    public AgentConfigurationObservation? Report()
    {
        var current = Volatile.Read(ref state);
        return current.Owner is null ? null : new(current.Owner, new(current.Applied?.Snapshot, current.Applied?.AppliedAt, current.Failure));
    }
    private sealed record State(AgentSettingsOwner? Owner, AgentAppliedSettings? Applied, AgentConfigurationFailure? Failure);
}
