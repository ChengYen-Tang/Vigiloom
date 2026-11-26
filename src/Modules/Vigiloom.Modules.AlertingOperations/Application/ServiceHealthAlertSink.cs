using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Notifications;
using Vigiloom.Contracts.Resources;
using Vigiloom.Contracts.Services;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application;

/// <summary>Records confirmed health changes for an individual service health target.</summary>
public sealed class ServiceHealthAlertSink(IServiceGroupSource groups) : IHealthTransitionSink
{
    public async Task RecordAsync(HealthTransition transition, DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        if (transition.State == ServiceHealthState.Unknown) return;
        var options = new DbContextOptionsBuilder<OperationsDbContext>()
            .UseSqlServer(connection, sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel)
                .MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
        await using var db = new OperationsDbContext(options);
        await db.Database.UseTransactionAsync(transaction, token);
        var alert = await db.ServiceHealthAlerts.SingleOrDefaultAsync(x => x.TargetId == transition.TargetId && x.RecoveredAt == null, token);
        if (transition.State == ServiceHealthState.Healthy)
        {
            if (alert is null) return;
            alert.RecoveredAt = transition.ObservedAt;
            alert.State = (int)transition.State;
        }
        else if (alert is null)
        {
            alert = new ServiceHealthAlert { TargetId = transition.TargetId, ServiceId = transition.ServiceId,
                InstanceId = transition.InstanceId, OpenedAt = transition.ObservedAt, State = (int)transition.State };
            db.ServiceHealthAlerts.Add(alert);
        }
        else
        {
            if (alert.State == (int)transition.State) return;
            alert.State = (int)transition.State;
        }
        var memberships = await groups.ReadAsync(transition.ServiceId, connection, transaction, token);
        var groupText = memberships.Count == 0 ? "None" : string.Join(", ", memberships.Select(x => x.Name));
        var title = transition.State switch
        {
            ServiceHealthState.Healthy => "Service health recovered",
            ServiceHealthState.Degraded => "Service health degraded",
            _ => "Service health unhealthy"
        };
        NotificationWriter.Add(db, new NotificationRequest(Guid.CreateVersion7(), "service-health", transition.ObservedAt, title,
            $"Service: {transition.ServiceName}; Environment: {transition.Environment}; Check: {transition.TargetId:D}; Groups: {groupText}",
            ResourceId: transition.ServiceId, EvidenceId: transition.TargetId));
        await db.SaveChangesAsync(token);
    }
}
