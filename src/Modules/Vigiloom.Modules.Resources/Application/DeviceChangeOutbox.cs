using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application;

/// <summary>Commits device changes together with durable notifications to affected services.</summary>
public sealed class DeviceChangeOutbox(ResourceWork outbox)
{
    public async Task CommitAsync(ResourcesDbContext db, Device device, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A device transaction must be active before publishing a change.");

        await db.SaveChangesAsync(token);
        await outbox.PublishAsync(db, new NetworkDeviceChanged(device.Id, device.Version), token);
        await outbox.PublishAsync(db, new AgentDeviceChanged(device.Id, device.Version), token);
        await db.Database.CurrentTransaction!.CommitAsync(token);
    }
}
