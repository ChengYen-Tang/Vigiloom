using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Hosting;

/// <summary>Keeps only each module's latest error, scoped to the Agent identity.</summary>
internal sealed class AgentModuleFailures(TimeProvider clock)
{
    private readonly object gate = new();
    private readonly Dictionary<AgentModule, (AgentSettingsOwner Owner, DateTimeOffset At)> failures = [];

    public void Record(AgentSettingsOwner? owner, AgentModule module)
    {
        if (owner is null) return;
        lock (gate) failures[module] = (owner, clock.GetUtcNow());
    }

    public AgentModuleFailure[] ForOwner(AgentSettingsOwner? owner)
    {
        if (owner is null) return [];
        lock (gate) return failures.Where(x => x.Value.Owner == owner)
            .Select(x => new AgentModuleFailure(x.Key, x.Value.At)).ToArray();
    }
}
