using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Logs;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Hosting.Logs;

/// <summary>Maintains bounded, identity-bound observations without turning unobserved sources into success.</summary>
internal sealed class AgentLogStatusRuntime(TimeProvider clock)
{
    private readonly object gate = new();
    private AgentSettingsOwner? owner;
    private string? revision;
    private bool throttled;
    private readonly Dictionary<Guid, Entry> sources = [];

    public void Bind(AgentSettingsOwner? next)
    {
        lock (gate)
        {
            if (owner == next) return;
            owner = next; revision = null; throttled = false; sources.Clear();
        }
    }

    public void Record(AgentSettingsOwner expected, AgentLogSettingsSnapshot snapshot, bool isThrottled,
        IReadOnlyList<LogSourceCycleResult> results, DateTimeOffset observedAt)
    {
        lock (gate)
        {
            if (owner != expected) return;
            if (revision != snapshot.Revision) { sources.Clear(); revision = snapshot.Revision; }
            throttled = isThrottled;
            var settings = snapshot.Sources.ToDictionary(x => x.SourceId);
            foreach (var result in results)
            {
                if (!settings.TryGetValue(result.SourceId, out var source) || !source.Enabled) continue;
                var state = result.Status switch
                {
                    LogSourceCycleStatus.End => AgentLogSourceState.End,
                    LogSourceCycleStatus.Queued => AgentLogSourceState.Collected,
                    LogSourceCycleStatus.Full => AgentLogSourceState.Full,
                    LogSourceCycleStatus.Unsupported => AgentLogSourceState.Unsupported,
                    LogSourceCycleStatus.Unavailable => AgentLogSourceState.Unavailable,
                    LogSourceCycleStatus.Conflict => AgentLogSourceState.Conflict,
                    _ => (AgentLogSourceState?)null
                };
                if (state is { } value) sources[result.SourceId] = new(new(result.SourceId, source.Generation, value,
                    (result.ObservedAt ?? observedAt).ToUniversalTime(), 0), result.Timestamp ?? clock.GetTimestamp());
            }
        }
    }

    public AgentLogStatusObservation? ForOwner(AgentSettingsOwner? expected)
    {
        lock (gate)
        {
            if (expected is null || owner != expected || revision is null) return null;
            var timestamp = clock.GetTimestamp();
            var observations = sources.Values.Select(entry => entry.Observation with
            {
                AgeSeconds = (int)Math.Clamp(clock.GetElapsedTime(entry.Timestamp, timestamp).TotalSeconds, 0, 2_592_000)
            }).ToArray();
            return new(expected, new AgentLogStatusReport(revision, throttled, observations));
        }
    }

    private sealed record Entry(AgentLogSourceObservation Observation, long Timestamp);
}
