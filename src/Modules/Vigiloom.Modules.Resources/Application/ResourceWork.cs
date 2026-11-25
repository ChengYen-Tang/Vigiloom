using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Resources;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application;

/// <summary>Persists resource changes for delivery with the owning business transaction.</summary>
public sealed class ResourceWork(TemporalWorkWriter writer, IConfiguration configuration)
{
    public Task<Guid> PublishAsync<T>(ResourcesDbContext db, T message, CancellationToken token) where T : notnull
    {
        var (setting, fallback) = message switch
        {
            NetworkDeviceChanged => ("NetworkQueue", "vigiloom.network.resources"),
            AgentDeviceChanged or AgentAssignmentChanged or AgentRegistrationChanged => ("AgentQueue", "vigiloom.agent.resources"),
            ApplicationApproved or ApplicationStateChanged or ApplicationPublicationChanged => ("ObservabilityQueue", "vigiloom.observability.catalog"),
            GroupAuditRecorded or AgentRegistrationAudit or AgentVersionAssignmentAudit or PublicTemplateAuditRecorded or
                PublicationSettingAudit or ApplicationApprovalAudit or ApplicationStateAudit => ("AuditQueue", "vigiloom.operations.audit"),
            _ => throw new ArgumentException("Unsupported resource work type.", nameof(message))
        };
        var queue = configuration[$"Work:{setting}"] ?? fallback;
        return writer.WriteAsync(db, queue, new ServiceWork(typeof(T).Name, JsonSerializer.Serialize(message)), token);
    }
}
