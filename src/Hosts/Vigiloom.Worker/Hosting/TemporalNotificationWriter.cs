using System.Data.Common;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.Data.SqlClient;
using Vigiloom.Contracts.Notifications;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Work;

namespace Vigiloom.Worker.Hosting;

/// <summary>Records security notifications alongside the source transaction for Operations delivery.</summary>
internal sealed class TemporalNotificationWriter(OutboxWriter outbox, IConfiguration configuration) : INotificationWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public Task WriteAsync(NotificationRequest request, DbConnection connection, DbTransaction transaction, CancellationToken token) =>
        WriteBatchAsync([request], connection, transaction, token);

    public async Task WriteBatchAsync(IReadOnlyList<NotificationRequest> requests, DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        if (requests.Count == 0) return;
        if (connection is not SqlConnection sql || transaction is not SqlTransaction sqlTransaction)
            throw new ArgumentException("An active SQL Server transaction is required.");
        var unique = new Dictionary<Guid, NotificationRequest>();
        foreach (var request in requests)
        {
            if (request.MessageId.Version != 7) throw new ArgumentException("Notification message IDs must be UUID v7.", nameof(requests));
            if (unique.TryGetValue(request.MessageId, out var previous) && previous != request)
                throw new InvalidOperationException("Different notifications use the same message ID.");
            unique[request.MessageId] = request;
        }
        var queue = configuration["Work:OperationsQueue"] ?? OperationsWork.Queue;
        foreach (var request in unique.Values)
        {
            var work = new ServiceWork(NotificationWork.Kind, JsonSerializer.Serialize(request, JsonOptions));
            var payload = JsonSerializer.Serialize(new TemporalWorkRequest(queue, work), JsonOptions);
            await outbox.WriteAsync(new(Guid.CreateVersion7(), TemporalOutboxHandler.Type, payload), sql, sqlTransaction, token);
        }
    }
}
