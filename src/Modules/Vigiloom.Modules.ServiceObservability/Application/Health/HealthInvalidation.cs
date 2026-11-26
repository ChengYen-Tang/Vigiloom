using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Domain.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Health;

/// <summary>Ends evidence at the actual authorization boundary in its owning transaction.</summary>
internal static class HealthInvalidation
{
    internal static async Task ServiceAsync(ServiceObservabilityDbContext db, Guid serviceId, DateTimeOffset at, CancellationToken token, IEnumerable<IHealthTransitionSink>? sinks = null)
        => await InvalidateAsync(db, serviceId, null, at, token, sinks);

    internal static async Task InstanceAsync(ServiceObservabilityDbContext db, Guid serviceId, Guid instanceId, DateTimeOffset at, CancellationToken token, IEnumerable<IHealthTransitionSink>? sinks = null)
        => await InvalidateAsync(db, serviceId, instanceId, at, token, sinks);

    private static async Task InvalidateAsync(ServiceObservabilityDbContext db, Guid serviceId, Guid? instanceId, DateTimeOffset at, CancellationToken token, IEnumerable<IHealthTransitionSink>? sinks)
    {
        var ids = await db.HealthTargets.AsNoTracking().Where(x => x.ServiceId == serviceId && x.Enabled && (instanceId == null || x.InstanceId == instanceId))
            .OrderBy(x => x.RowId).Select(x => x.Id).ToArrayAsync(token);
        foreach (var id in ids)
        {
            var target = await db.HealthTargets.FromSqlInterpolated($"SELECT * FROM [service].[HealthTargets] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}").SingleAsync(token);
            var policy = await LockPolicyAsync(db, token);
            var schedule = await db.HealthSchedules.FromSqlInterpolated($"SELECT * FROM [service].[HealthSchedules] WITH (UPDLOCK, HOLDLOCK) WHERE [TargetId] = {id}").SingleOrDefaultAsync(token);
            if (schedule is null || !schedule.Enabled) continue;
            var changed = schedule.State != (byte)EvaluatedHealth.Unknown;
            var transitionAt = at;
            if (changed)
            {
                var (boundary, reason) = await BoundaryAsync(db, target, schedule, policy, at, token);
                var start = await db.HealthStateIntervals.AsNoTracking().Where(x => x.TargetId == id && x.EndedAt == null)
                    .Select(x => (DateTimeOffset?)x.StartedAt).SingleOrDefaultAsync(token);
                if (start is null) throw new InvalidOperationException("The current health interval is missing.");
                // If the database clock moved backwards, retain ordering while revoking immediately.
                // The effective historical boundary cannot be earlier than the existing interval.
                if (boundary < start) { boundary = start.Value; reason = 9; }
                transitionAt = boundary;
                var closed = await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[HealthStateIntervals] SET [EndedAt] = {boundary} WHERE [TargetId] = {id} AND [EndedAt] IS NULL AND [StartedAt] <= {boundary}", token);
                if (closed != 1) throw new InvalidOperationException("The current health interval is missing or out of order.");
                db.HealthStateIntervals.Add(new HealthStateInterval(id, (byte)EvaluatedHealth.Unknown, reason, boundary));
            }
            schedule.Reset(true, at);
            if (changed) await RecordUnknownAsync(db, target, schedule, transitionAt, sinks, token);
        }
    }

    internal static async Task RecordUnknownAsync(ServiceObservabilityDbContext db, HealthTarget target,
        HealthSchedule schedule, DateTimeOffset at, IEnumerable<IHealthTransitionSink>? sinks, CancellationToken token)
    {
        if (sinks is null) return;
        var service = db.Services.Local.SingleOrDefault(x => x.Id == target.ServiceId)
            ?? await db.Services.SingleAsync(x => x.Id == target.ServiceId, token);
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("An active health transaction is required.");
        var transition = new HealthTransition(target.Id, target.ServiceId, target.InstanceId, schedule.Generation,
            ServiceHealthState.Unknown, at, service.DisplayName, service.Environment, schedule.LastTransitionGeneration);
        foreach (var sink in sinks)
            await sink.RecordAsync(transition, db.Database.GetDbConnection(), transaction, token);
        schedule.LastTransitionGeneration = schedule.Generation;
    }

    internal static Task<HealthNetworkPolicy?> LockPolicyAsync(ServiceObservabilityDbContext db, CancellationToken token) =>
        db.HealthNetworkPolicies.FromSqlRaw("SELECT * FROM [service].[HealthNetworkPolicies] WITH (HOLDLOCK) WHERE [Name] = 'central'").SingleOrDefaultAsync(token);

    internal static async Task<(DateTimeOffset Boundary, byte Reason)> BoundaryAsync(ServiceObservabilityDbContext db,
        HealthTarget target, HealthSchedule schedule, HealthNetworkPolicy? policy, DateTimeOffset at, CancellationToken token)
    {
        var deadline = schedule.EvidenceAt!.Value.AddSeconds(2 * target.IntervalSeconds + target.TimeoutSeconds);
        var boundary = deadline < at ? deadline : at;
        byte reason = deadline < at ? (byte)8 : (byte)9;
        if (policy is not null && schedule.LastEvaluatedAt is { } prior &&
            (schedule.NetworkVersion is null || !schedule.NetworkVersion.AsSpan().SequenceEqual(policy.Version)))
        {
            var changedAt = await HealthStore.PolicyBoundaryAsync(db, target.Id, policy.Id, policy.Generation, prior, token);
            if (changedAt < boundary) { boundary = changedAt; reason = 9; }
        }
        return (boundary, reason);
    }
}
