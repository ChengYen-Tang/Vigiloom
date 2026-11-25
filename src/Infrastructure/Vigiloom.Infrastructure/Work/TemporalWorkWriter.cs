using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Infrastructure.Scheduling.Outbox;

namespace Vigiloom.Infrastructure.Work;

/// <summary>Enqueues one durable delivery to one destination in the caller's business transaction.</summary>
public sealed class TemporalWorkWriter(OutboxWriter outbox)
{
    public async Task<Guid> WriteAsync(DbContext db, string taskQueue, ServiceWork work, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskQueue);
        ArgumentNullException.ThrowIfNull(work);
        var deliveryId = Guid.CreateVersion7();
        var payload = JsonSerializer.Serialize(new TemporalWorkRequest(taskQueue, work));
        await outbox.WriteAsync(new(deliveryId, TemporalOutboxHandler.Type, payload), db, cancellationToken);
        return deliveryId;
    }
}
