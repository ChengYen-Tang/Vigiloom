using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Persistence;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace Vigiloom.Modules.Resources.Application;

public sealed class GroupAuditOutbox(IDbContextOutbox outbox)
{
    public async Task CommitAsync(ResourcesDbContext db, OperationAudit audit, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null || audit.Details is not GroupChanged group)
            throw new InvalidOperationException("A group transaction and group audit are required.");
        outbox.Enroll(db);
        await outbox.PublishAsync(new GroupAuditRecorded(audit.ActorId, audit.ResourceId, audit.OccurredAt, group.Action, group.Added, group.Removed),
            new DeliveryOptions { DeduplicationId = Guid.CreateVersion7().ToString("N") });
        await outbox.SaveChangesAndFlushMessagesAsync(token);
    }
}
