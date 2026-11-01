using Microsoft.EntityFrameworkCore;
using Vigiloom.Infrastructure.Scheduling.Persistence;

namespace Vigiloom.Infrastructure.Scheduling;

/// <summary>Durably records intent before a Dashboard mutation, then its observed HTTP outcome.</summary>
public sealed class DashboardAuditStore(IDbContextFactory<SchedulingDbContext> contexts)
{
    public async Task<long[]> BeginAsync(Guid actor, string action, IReadOnlyList<string> jobIds, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var now = DateTimeOffset.UtcNow;
        var records = jobIds.Select(id => new DashboardAuditRecord { ActorId = actor, OccurredAt = now, Action = action, JobId = id, Result = "started" }).ToArray();
        db.DashboardAudit.AddRange(records);
        await db.SaveChangesAsync(token);
        return records.Select(record => record.RowId).ToArray();
    }

    public async Task CompleteAsync(long[] ids, string result, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await db.DashboardAudit.Where(record => ids.Contains(record.RowId))
            .ExecuteUpdateAsync(update => update.SetProperty(record => record.Result, result), token);
    }
}
