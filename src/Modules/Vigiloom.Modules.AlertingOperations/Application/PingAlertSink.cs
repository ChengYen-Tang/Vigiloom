using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Notifications;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application;

/// <summary>Records target outages and recovery in the measurement transaction.</summary>
public sealed class PingAlertSink : IPingTransitionSink
{
    public async Task RecordAsync(PingTransition transition, DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        if (transition.Current is not (Reachability.Unreachable or Reachability.Reachable)) return;
        var options = new DbContextOptionsBuilder<OperationsDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
        await using var db = new OperationsDbContext(options);
        await db.Database.UseTransactionAsync(transaction, token);
        var alert = await db.PingAlerts.SingleOrDefaultAsync(x => x.DeviceId == transition.DeviceId &&
            x.Target == transition.Target && x.RecoveredAt == null, token);
        if (transition.Current == Reachability.Unreachable)
        {
            if (alert is not null) return;
            alert = new PingAlert { DeviceId = transition.DeviceId, Target = transition.Target, OpenedAt = transition.OccurredAt };
            db.PingAlerts.Add(alert);
        }
        else
        {
            if (alert is null) return;
            alert.RecoveredAt = transition.OccurredAt;
        }
        var alertEvent = new PingAlertEvent { AlertId = alert.Id, Generation = transition.Generation,
            OccurredAt = transition.OccurredAt, IsRecovery = transition.Current == Reachability.Reachable,
            DeviceName = transition.Device.Name, GroupsJson = JsonSerializer.Serialize(transition.Device.Groups) };
        db.PingAlertEvents.Add(alertEvent);
        var groups = transition.Device.Groups.Count == 0 ? "None" : string.Join(", ", transition.Device.Groups.Select(x => x.Name));
        var title = transition.Current == Reachability.Reachable ? "Ping recovered" : "Ping unreachable";
        NotificationWriter.Add(db, new NotificationRequest(alertEvent.MessageId, "ping", transition.OccurredAt, title,
            $"Device: {transition.Device.Name}; Target: {transition.Target}; Groups: {groups}", ResourceId: transition.DeviceId));
        await db.SaveChangesAsync(token);
    }
}
