using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application.Line;

/// <summary>Schedules new logical notifications through the shared transactional outbox.</summary>
public sealed class LineDeliveryQueue(IDbContextFactory<OperationsDbContext> contexts, OutboxWriter outbox, TimeProvider clock)
{
    public async Task<Guid?> EnqueueTestAsync(Guid groupId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var group = await db.LineGroups.AsNoTracking().SingleOrDefaultAsync(x => x.Id == groupId && x.Approved, token);
        var configured = await db.LineSettings.AsNoTracking().AnyAsync(x => x.ProtectedAccessToken != null, token);
        if (group is null || !configured) return null;
        var delivery = new LineDelivery { GroupId = group.Id, Recipient = group.ExternalId,
            Text = "Vigiloom test notification: LINE notifications are connected.", CreatedAt = clock.GetUtcNow() };
        db.LineDeliveries.Add(delivery);
        await db.SaveChangesAsync(token);
        await outbox.WriteAsync(new(delivery.Id, LineDeliveryHandler.Type, "{}"),
            (SqlConnection)db.Database.GetDbConnection(), (SqlTransaction)transaction.GetDbTransaction(), token);
        await transaction.CommitAsync(token);
        return delivery.Id;
    }

    public async Task EnqueueNewAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var settings = await db.LineSettings.FromSqlRaw("SELECT * FROM [operations].[LineSettings] WITH (UPDLOCK, HOLDLOCK) WHERE [Slot] = 1")
            .SingleOrDefaultAsync(token);
        if (settings is not { Enabled: true }) return;

        var notifications = await db.Notifications.AsNoTracking().Where(x => x.RowId > settings.LastNotificationRowId)
            .OrderBy(x => x.RowId).Take(100).ToArrayAsync(token);
        if (notifications.Length == 0) return;
        var now = clock.GetUtcNow();
        if (settings.AggregationStartedAt is null)
        {
            settings.AggregationStartedAt = now;
            if (settings.AggregationSeconds > 0)
            {
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
                return;
            }
        }
        if (now < settings.AggregationStartedAt.Value.AddSeconds(settings.AggregationSeconds)) return;
        var groups = await db.LineGroups.AsNoTracking().Where(x => x.Approved).ToArrayAsync(token);
        var deliveries = new List<LineDelivery>();
        var text = new System.Text.StringBuilder();
        Guid firstId = default;
        void Flush()
        {
            if (text.Length == 0) return;
            var message = text.ToString();
            foreach (var group in groups)
                deliveries.Add(new LineDelivery { NotificationId = firstId, GroupId = group.Id,
                    Recipient = group.ExternalId, Text = message, CreatedAt = now });
            text.Clear();
        }
        foreach (var notification in notifications)
        {
            var entry = $"{notification.Title}\n{notification.Detail}";
            if (entry.Length > 5000)
            {
                var length = char.IsHighSurrogate(entry[4999]) ? 4999 : 5000;
                entry = entry[..length];
            }
            if (text.Length > 0 && text.Length + 2 + entry.Length > 5000) Flush();
            if (text.Length == 0) firstId = notification.Id;
            else text.Append("\n\n");
            text.Append(entry);
        }
        Flush();
        db.LineDeliveries.AddRange(deliveries);
        settings.LastNotificationRowId = notifications[^1].RowId;
        settings.AggregationStartedAt = null;
        await db.SaveChangesAsync(token);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        var sqlTransaction = (SqlTransaction)transaction.GetDbTransaction();
        foreach (var delivery in deliveries)
            await outbox.WriteAsync(new(delivery.Id, LineDeliveryHandler.Type, "{}"), connection, sqlTransaction, token);
        await transaction.CommitAsync(token);
    }
}
