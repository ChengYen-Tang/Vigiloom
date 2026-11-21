using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application.Retention;

/// <summary>Preserves open alerts and notifications not yet queued for an enabled channel.</summary>
internal static class AlertRetention
{
    internal static async Task<(long Episodes, long Notifications)> CountAsync(OperationsDbContext db, DateTimeOffset cutoff, CancellationToken token)
    {
        var cursor = await PendingCursorAsync(db, token);
        var ping = await db.PingAlerts.LongCountAsync(x => x.RecoveredAt != null && x.RecoveredAt < cutoff, token);
        var service = await db.ServiceHealthAlerts.LongCountAsync(x => x.RecoveredAt != null && x.RecoveredAt < cutoff, token);
        var notifications = await EligibleNotifications(db, cutoff, cursor).LongCountAsync(token);
        return (ping + service, notifications);
    }

    internal static async Task<int> DeleteBatchAsync(OperationsDbContext db, DateTimeOffset cutoff, int batchSize, CancellationToken token)
    {
        var cursor = await PendingCursorAsync(db, token);
        var ping = db.PingAlerts.Where(x => x.RecoveredAt != null && x.RecoveredAt < cutoff)
            .OrderBy(x => x.RecoveredAt).ThenBy(x => x.RowId).Select(x => x.Id).Take(batchSize);
        var removed = await db.PingAlerts.Where(x => ping.Contains(x.Id)).ExecuteDeleteAsync(token);
        if (removed < batchSize)
        {
            var service = db.ServiceHealthAlerts.Where(x => x.RecoveredAt != null && x.RecoveredAt < cutoff)
                .OrderBy(x => x.RecoveredAt).ThenBy(x => x.RowId).Select(x => x.Id).Take(batchSize - removed);
            removed += await db.ServiceHealthAlerts.Where(x => service.Contains(x.Id)).ExecuteDeleteAsync(token);
        }
        if (removed < batchSize)
        {
            var notifications = EligibleNotifications(db, cutoff, cursor).OrderBy(x => x.OccurredAt).ThenBy(x => x.RowId)
                .Select(x => x.Id).Take(batchSize - removed);
            removed += await db.Notifications.Where(x => notifications.Contains(x.Id)).ExecuteDeleteAsync(token);
        }
        return removed;
    }

    private static IQueryable<Domain.Notification> EligibleNotifications(OperationsDbContext db, DateTimeOffset cutoff, long? cursor) =>
        db.Notifications.Where(x => x.OccurredAt < cutoff && (cursor == null || x.RowId <= cursor));

    private static async Task<long?> PendingCursorAsync(OperationsDbContext db, CancellationToken token)
    {
        // LINE changes its cursor under the same row lock before queuing notifications.
        var settings = await db.LineSettings.FromSqlRaw("SELECT * FROM [operations].[LineSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [Slot] = 1")
            .SingleOrDefaultAsync(token);
        return settings is { Enabled: true } ? settings.LastNotificationRowId : null;
    }
}
