using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class HostLogEvidenceReader(HttpClient client, IConfiguration configuration, IDbContextFactory<AgentManagementDbContext> contexts)
{
    public async Task<HostLogEvidenceView?> GetAsync(Guid deviceId, Guid agentId, Guid messageId, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(configuration["Services:Operations:Url"]))
        {
            using var response = await client.GetAsync($"internal/v1/host-logs/{deviceId:D}/{agentId:D}/{messageId:D}", token);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<HostLogEvidenceView>(cancellationToken: token)
                ?? throw new InvalidOperationException("Operations returned an empty host log evidence response.");
        }
        await using var db = await contexts.CreateDbContextAsync(token);
        return await (from log in db.HostLogs.AsNoTracking()
            join agent in db.Agents.AsNoTracking() on log.AgentId equals agent.Id
            where agent.DeviceId == deviceId && log.AgentId == agentId && log.MessageId == messageId
            select new HostLogEvidenceView(log.SourceName, log.ParserVersion, log.ReceivedAt, log.Content)).FirstOrDefaultAsync(token);
    }
}
