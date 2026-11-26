using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Resources;
using Vigiloom.Contracts.Agent.Security;
using Vigiloom.Infrastructure.Work;

namespace Vigiloom.HostMonitoring;

/// <summary>Handles resource changes delivered to host monitoring.</summary>
public sealed class AgentChangeHandler(IHubContext<AgentChannel> hub) : IServiceWorkHandler
{
    public async Task<string?> ExecuteAsync(ServiceWork work, CancellationToken cancellationToken)
    {
        // Eligibility and credential state are checked against Resources for every Agent request.
        // There is no host-side copy to refresh when a device or registration changes.
        if (work.Kind is nameof(AgentDeviceChanged) or nameof(AgentRegistrationChanged)) return null;
        if (work.Kind == nameof(YaraScanChanged))
        {
            var scan = JsonSerializer.Deserialize<YaraScanChanged>(work.Payload) ?? throw new ArgumentException("Scan notification is empty.");
            await hub.Clients.Group(AgentChannel.Group(scan.AgentId)).SendAsync("ScanAvailable", cancellationToken);
            return null;
        }
        if (work.Kind != nameof(AgentAssignmentChanged)) throw new ArgumentException("Unsupported Agent work type.");
        var change = JsonSerializer.Deserialize<AgentAssignmentChanged>(work.Payload)
            ?? throw new ArgumentException("Agent assignment is empty.");
        await hub.Clients.Group(AgentChannel.Group(change.AgentId)).SendAsync("UpdateAvailable", cancellationToken);
        return null;
    }
}
