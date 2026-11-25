using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Wolverine.Attributes;

namespace Vigiloom.Web.Services;

/// <summary>Applies resource-service changes to the transitional observation database.</summary>
public sealed class ApplicationEventHandler
{
    [NonTransactional]
    public static async Task Handle(ApplicationApproved message, IDbContextFactory<ServiceObservabilityDbContext> contexts,
        IHttpClientFactory clients, ApplicationStateProjection projection, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var source = await db.Sources.FromSqlInterpolated($"SELECT * FROM [service].[Sources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {message.SourceId}")
            .SingleOrDefaultAsync(token);
        if (source is null) throw new InvalidOperationException("Application source is not yet available.");
        var existing = await db.Services.FromSqlInterpolated($"SELECT * FROM [service].[Services] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {message.ServiceId}")
            .SingleOrDefaultAsync(token);
        if (existing is null)
        {
            var service = MonitoredService.FromResource(message.ServiceId, message.SourceId, message.ServiceKey,
                message.Environment, message.DisplayName, message.ApprovedAt, message.ServiceNamespace);
            // A delayed approval must not reopen collection before current resource state is checked.
            service.Revoke(message.ApprovedAt);
            db.Services.Add(service);
            await db.SaveChangesAsync(token);
        }
        await tx.CommitAsync(token);
        var remote = new RemoteApplicationManagement(clients.CreateClient(nameof(RemoteApplicationManagement)), projection);
        await projection.ApplyAsync(message.ServiceId, remote.GetServiceAsync, token);
    }

    [NonTransactional]
    public static Task Handle(ApplicationStateChanged message, IHttpClientFactory clients, ApplicationStateProjection projection, CancellationToken token)
    {
        var remote = new RemoteApplicationManagement(clients.CreateClient(nameof(RemoteApplicationManagement)), projection);
        return projection.ApplyAsync(message.ServiceId, remote.GetServiceAsync, token);
    }

    [NonTransactional]
    public static Task Handle(ApplicationPublicationChanged message, ApplicationStateProjection projection, CancellationToken token) =>
        projection.InvalidatePublicationAsync(message.ServiceId, token);
}
