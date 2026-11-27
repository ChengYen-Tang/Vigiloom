using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Security;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Modules.AgentManagement.Application;

public sealed class HostSecurityWorkWriter(TemporalWorkWriter work, IConfiguration configuration) : IHostSecurityWorkWriter
{
    public async Task WriteAsync(HostMonitoringDbContext db, HostSecurityBatchReady batch, CancellationToken token) =>
        await work.WriteAsync(db, configuration["Work:SecurityQueue"] ?? "vigiloom.security.agent",
            new ServiceWork(nameof(HostSecurityBatchReady), JsonSerializer.Serialize(batch)), token);

    public async Task WriteStartAsync(HostMonitoringDbContext db, YaraScanStarted started, CancellationToken token) =>
        await work.WriteAsync(db, configuration["Work:SecurityQueue"] ?? "vigiloom.security.agent",
            new ServiceWork(nameof(YaraScanStarted), JsonSerializer.Serialize(started)), token);
}
