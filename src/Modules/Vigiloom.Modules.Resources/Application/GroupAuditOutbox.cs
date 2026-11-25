using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Persistence;

namespace Vigiloom.Modules.Resources.Application;

public sealed class GroupAuditOutbox(ResourceWork outbox)
{
    public async Task CommitAsync(ResourcesDbContext db, OperationAudit audit, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null || audit.Details is not GroupChanged group)
            throw new InvalidOperationException("A group transaction and group audit are required.");

        await outbox.PublishAsync(db, new GroupAuditRecorded(audit.ActorId, audit.ResourceId, audit.OccurredAt, group.Action, group.Added, group.Removed), token);
        await db.Database.CurrentTransaction!.CommitAsync(token);
    }
}
