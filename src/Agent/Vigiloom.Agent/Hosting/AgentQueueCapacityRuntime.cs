using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Hosting;

/// <summary>Tracks synchronization failure separately from the transport scheduling group.</summary>
internal sealed class AgentQueueCapacityRuntime
{
    private State state = new(null, null);
    public void Bind(AgentSettingsOwner? owner)
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (current.Owner == owner) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref state, new(owner, null), current), current)) return;
        }
    }
    public void SetFailure(AgentSettingsOwner? owner, AgentConfigurationFailure? failure)
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (current.Owner != owner) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref state, new(owner, failure), current), current)) return;
        }
    }
    public AgentQueueCapacityObservation? Report()
    {
        var current = Volatile.Read(ref state);
        return current.Owner is null ? null : new(current.Owner, current.Failure);
    }
    private sealed record State(AgentSettingsOwner? Owner, AgentConfigurationFailure? Failure);
}
