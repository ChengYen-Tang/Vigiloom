using System.Text.Json;
using Vigiloom.Contracts.Notifications;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Events;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Application.Timeline;

namespace Vigiloom.Operations.Work;

/// <summary>Persists Operations work and its Temporal delivery receipt in one transaction.</summary>
public sealed class OperationsWorkHandler(IConfiguration configuration, NotificationWriter notifications, OperationAuditWriter audits, TimelineWriter timeline) : IServiceWorkHandler
{
    public Task<string?> ExecuteAsync(ServiceWork work, CancellationToken cancellationToken)
    {
        var connection = configuration.GetConnectionString("Operations")
            ?? throw new InvalidOperationException("ConnectionStrings:Operations is required.");
        var handler = new TransactionalWorkHandler(connection, async (item, db, transaction, token) =>
        {
            if (item.Kind == NotificationWork.Kind)
            {
                var request = JsonSerializer.Deserialize<NotificationRequest>(item.Payload)
                    ?? throw new InvalidOperationException("Notification work is empty.");
                await notifications.WriteAsync(request, db, transaction, token);
            }
            else if (item.Kind == OperationAuditWork.Kind)
            {
                var audit = JsonSerializer.Deserialize<OperationAudit>(item.Payload)
                    ?? throw new InvalidOperationException("Audit work is empty.");
                await audits.AppendAsync(db, transaction, audit, token);
            }
            else if (item.Kind == EventTimelineWork.Kind)
            {
                var delivery = JsonSerializer.Deserialize<EventTimelineDelivery>(item.Payload)
                    ?? throw new InvalidOperationException("Timeline work is empty.");
                await timeline.AppendAsync(delivery, db, transaction, token);
            }
            else await audits.AppendAsync(db, transaction, ResourceAudit.Convert(item), token);
            return null;
        });
        return handler.ExecuteAsync(work, cancellationToken);
    }
}
