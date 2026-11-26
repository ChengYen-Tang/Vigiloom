using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace Vigiloom.HostMonitoring;

public sealed class AgentChannel : Hub
{
    public static string Group(Guid agentId) => $"agent:{agentId:D}";

    public override async Task OnConnectedAsync()
    {
        if (!Guid.TryParse(Context.User?.FindFirstValue(ClaimTypes.NameIdentifier), out var agentId))
        {
            Context.Abort();
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(agentId));
        await base.OnConnectedAsync();
    }
}
