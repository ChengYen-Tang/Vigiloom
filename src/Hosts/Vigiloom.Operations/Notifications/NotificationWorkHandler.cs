using System.Text.Json;
using Vigiloom.Contracts.Notifications;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.AlertingOperations.Application;

namespace Vigiloom.Operations.Notifications;

/// <summary>Persists a notification and its Temporal delivery receipt in one transaction.</summary>
public sealed class NotificationWorkHandler(IConfiguration configuration, NotificationWriter notifications) : IServiceWorkHandler
{
    public Task<string?> ExecuteAsync(ServiceWork work, CancellationToken cancellationToken)
    {
        var connection = configuration.GetConnectionString("Operations")
            ?? throw new InvalidOperationException("ConnectionStrings:Operations is required.");
        var handler = new TransactionalWorkHandler(connection, async (item, db, transaction, token) =>
        {
            if (item.Kind != NotificationWork.Kind) throw new InvalidOperationException("Unsupported notification work type.");
            var request = JsonSerializer.Deserialize<NotificationRequest>(item.Payload)
                ?? throw new InvalidOperationException("Notification work is empty.");
            await notifications.WriteAsync(request, db, transaction, token);
            return null;
        });
        return handler.ExecuteAsync(work, cancellationToken);
    }
}
