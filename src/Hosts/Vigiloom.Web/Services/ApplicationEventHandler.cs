using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using System.Text.Json;
using System.Security.Cryptography;
using Vigiloom.Infrastructure.Work;

namespace Vigiloom.Web.Services;

/// <summary>Applies resource-service changes to the transitional observation database.</summary>
public sealed class ApplicationEventHandler(IDbContextFactory<ServiceObservabilityDbContext> contexts,
    IHttpClientFactory clients, ApplicationStateProjection projection) : IServiceWorkHandler
{
    public async Task<string?> ExecuteAsync(ServiceWork work, CancellationToken cancellationToken)
    {
        switch (work.Kind)
        {
            case nameof(ApplicationApproved):
                await Handle(JsonSerializer.Deserialize<ApplicationApproved>(work.Payload) ?? throw new InvalidOperationException("Missing application work."), contexts, clients, projection, cancellationToken);
                break;
            case nameof(ApplicationStateChanged):
                await Handle(JsonSerializer.Deserialize<ApplicationStateChanged>(work.Payload) ?? throw new InvalidOperationException("Missing application work."), clients, projection, cancellationToken);
                break;
            case nameof(ApplicationPublicationChanged):
                if (!Guid.TryParse(work.DeliveryId, out var deliveryId) || deliveryId.Version != 7)
                    throw new ArgumentException("A work delivery ID is required.");
                await projection.InvalidatePublicationAsync(
                    (JsonSerializer.Deserialize<ApplicationPublicationChanged>(work.Payload) ?? throw new InvalidOperationException("Missing application work.")).ServiceId,
                    cancellationToken, deliveryId, SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { work.Kind, work.Payload })));
                break;
            default: throw new InvalidOperationException("Unsupported application work type.");
        }
        return null;
    }

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

    public static Task Handle(ApplicationStateChanged message, IHttpClientFactory clients, ApplicationStateProjection projection, CancellationToken token)
    {
        var remote = new RemoteApplicationManagement(clients.CreateClient(nameof(RemoteApplicationManagement)), projection);
        return projection.ApplyAsync(message.ServiceId, remote.GetServiceAsync, token);
    }

}
