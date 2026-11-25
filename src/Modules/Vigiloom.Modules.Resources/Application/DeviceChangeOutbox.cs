using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Vigiloom.Modules.Resources.Application;

/// <summary>Commits device changes together with durable notifications to affected services.</summary>
public sealed class DeviceChangeOutbox(IDbContextOutbox outbox)
{
    public async Task CommitAsync(ResourcesDbContext db, Device device, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A device transaction must be active before publishing a change.");
        outbox.Enroll(db);
        var messageId = Guid.CreateVersion7().ToString("N");
        await outbox.PublishAsync(new NetworkDeviceChanged(device.Id, device.Version), new DeliveryOptions { DeduplicationId = messageId });
        await outbox.PublishAsync(new AgentDeviceChanged(device.Id, device.Version), new DeliveryOptions { DeduplicationId = messageId });
        await outbox.SaveChangesAndFlushMessagesAsync(token);
    }
}
