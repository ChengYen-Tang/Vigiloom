using System.Text.Json;
using Vigiloom.Contracts.Agent.Security;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;
using Vigiloom.Modules.SecurityAnalysis.Persistence;

namespace Vigiloom.Security;

internal sealed class YaraScanNotifier(TemporalWorkWriter writer, IConfiguration configuration) : IYaraScanNotifier
{
    public async Task NotifyAsync(SecurityAnalysisDbContext db, Guid agentId, CancellationToken token) =>
        await writer.WriteAsync(db, configuration["Work:AgentQueue"] ?? "vigiloom.agent.resources",
            new ServiceWork(nameof(YaraScanChanged), JsonSerializer.Serialize(new YaraScanChanged(agentId))), token);
}
