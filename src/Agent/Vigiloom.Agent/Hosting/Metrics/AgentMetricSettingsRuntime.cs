using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Hosting.Metrics;

/// <summary>Identity-bound last-good metric policy; saving does not imply that sampling applied it.</summary>
internal sealed class AgentMetricSettingsRuntime
{
    private State state = new(null, null, null);
    private LossWriter? lossWriter;
    public AgentMetricSettings Current => Volatile.Read(ref state).Saved?.Snapshot.Settings ?? AgentMetricSettings.Default;

    public AgentMetricSettings? ForOwner(AgentSettingsOwner owner)
    {
        var current = Volatile.Read(ref state);
        return current.Owner == owner ? current.Saved?.Snapshot.Settings ?? AgentMetricSettings.Default : null;
    }

    public Guid? LossWriterFor(AgentSettingsOwner? owner)
    {
        var current = Volatile.Read(ref lossWriter);
        return owner is not null && current?.Owner == owner ? current.Writer : null;
    }

    public void PublishLossWriter(AgentSettingsOwner owner, Guid writer) => Volatile.Write(ref lossWriter, new(owner, writer));

    public void ClearLossWriter(AgentSettingsOwner owner)
    {
        while (true)
        {
            var current = Volatile.Read(ref lossWriter);
            if (current is null || current.Owner != owner) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref lossWriter, null, current), current)) return;
        }
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

    public bool NeedsRestore(AgentSettingsOwner owner)
    {
        var current = Volatile.Read(ref state);
        return current.Owner == owner && current.Saved is null;
    }

    // A delayed local read must neither replace a newer value nor clear a synchronization error.
    public void RestoreIfEmpty(AgentSettingsOwner? owner, AgentSavedMetricSettings saved)
    {
        while (true)
        {
            var current = Volatile.Read(ref state);
            if (owner is null || current.Owner != owner || current.Saved is not null || saved.AgentId != owner.AgentId ||
                !string.Equals(saved.Server, owner.Server, StringComparison.Ordinal)) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref state, current with { Saved = saved }, current), current)) return;
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
    private sealed record LossWriter(AgentSettingsOwner Owner, Guid Writer);
}
