using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Health;

/// <summary>Calculates current service health from approved target evidence.</summary>
public sealed class HealthStatusReader(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    public async Task<ServiceHealthStatus?> ReadAsync(Guid serviceId, CancellationToken token)
    {
        if (serviceId.Version != 7) throw new ArgumentException("Invalid service.", nameof(serviceId));
        await using var db = await contexts.CreateDbContextAsync(token);
        if (!await db.Services.AsNoTracking().AnyAsync(x => x.Id == serviceId, token)) return null;

        // Targets have a per-service limit. A single query captures all versions together so
        // a new authorization or network policy cannot reuse evidence from an old one.
        var rows = await (from target in db.HealthTargets.AsNoTracking()
                          join service in db.Services.AsNoTracking() on target.ServiceId equals service.Id
                          join source in db.Sources.AsNoTracking() on service.SourceId equals source.Id
                          join schedule in db.HealthSchedules.AsNoTracking() on target.Id equals schedule.TargetId into schedules
                          from schedule in schedules.DefaultIfEmpty()
                          join instance in db.Instances.AsNoTracking() on target.InstanceId equals (Guid?)instance.Id into instances
                          from instance in instances.DefaultIfEmpty()
                          from policy in db.HealthNetworkPolicies.AsNoTracking().Where(x => x.Name == "central").DefaultIfEmpty()
                          where target.ServiceId == serviceId && target.Enabled
                          select new { target.IntervalSeconds, target.TimeoutSeconds, TargetVersion = target.Version,
                              ServiceVersion = service.Version, ServiceRevoked = service.RevokedAt,
                              SourceVersion = source.Version, SourceRevoked = source.RevokedAt,
                              InstanceId = target.InstanceId, InstanceVersion = instance == null ? null : instance.Version,
                              InstanceRetired = instance == null ? null : instance.RetiredAt,
                              PolicyVersion = policy == null ? null : policy.Version, Schedule = schedule }).ToArrayAsync(token);
        var now = await DiscoveryTransaction.NowAsync(db, token);
        var states = new EvaluatedHealth[rows.Length];
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var schedule = row.Schedule;
            if (schedule is null || !schedule.Enabled || row.ServiceRevoked is not null || row.SourceRevoked is not null ||
                schedule.LastEvaluatedAt > now || schedule.EvidenceAt > now ||
                (row.InstanceId is not null && (row.InstanceVersion is null || row.InstanceRetired is not null)) ||
                !Same(schedule.SourceVersion, row.SourceVersion) || !Same(schedule.ServiceVersion, row.ServiceVersion) ||
                !Same(schedule.TargetVersion, row.TargetVersion) || !Same(schedule.InstanceVersion, row.InstanceVersion) ||
                row.PolicyVersion is null || !Same(schedule.NetworkVersion, row.PolicyVersion)) continue;
            var state = new HealthEvaluationState((EvaluatedHealth)schedule.State, schedule.EvidenceAt,
                (HealthStreak)schedule.Streak, schedule.StreakCount, schedule.StreakAt, schedule.LastEvaluatedAt);
            states[i] = HealthEvaluation.Expire(state, now, row.IntervalSeconds, row.TimeoutSeconds).State.State;
        }
        var aggregate = HealthAggregation.Combine(states);
        return new((ServiceHealthState)aggregate.State, aggregate.Participants, aggregate.Unknown);
    }

    private static bool Same(byte[]? left, byte[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
}
