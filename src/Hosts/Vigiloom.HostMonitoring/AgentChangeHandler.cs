using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.Work;

namespace Vigiloom.HostMonitoring;

/// <summary>Signals an Agent after the desired version has been saved by Resources.</summary>
public sealed class AgentChangeHandler(IHubContext<AgentChannel> hub) : IServiceWorkHandler
{
    public async Task<string?> ExecuteAsync(ServiceWork work, CancellationToken cancellationToken)
    {
        if (work.Kind != nameof(AgentAssignmentChanged)) throw new ArgumentException("Unsupported Agent work type.");
        var change = JsonSerializer.Deserialize<AgentAssignmentChanged>(work.Payload)
            ?? throw new ArgumentException("Agent assignment is empty.");
        await hub.Clients.Group(AgentChannel.Group(change.AgentId)).SendAsync("UpdateAvailable", cancellationToken);
        return null;
    }
}
