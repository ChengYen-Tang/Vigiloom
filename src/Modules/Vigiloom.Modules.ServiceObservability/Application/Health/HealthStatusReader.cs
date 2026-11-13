using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.ServiceObservability.Domain.Health;

namespace Vigiloom.Modules.ServiceObservability.Application.Health;

/// <summary>Calculates current service health from approved target evidence.</summary>
public sealed class HealthStatusReader(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    public async Task<ServiceHealthStatus?> ReadAsync(Guid serviceId, CancellationToken token)
    {
        var statuses = await ReadManyAsync([serviceId], token);
        return statuses.GetValueOrDefault(serviceId)?.Status;
    }

    public sealed record OverviewStatus(ServiceHealthStatus Status, DateTimeOffset? LastEvaluatedAt, bool CollectionStopped, bool SourceRevoked);

    /// <summary>Uses the same evidence and freshness rules for a bounded page of services.</summary>
    public async Task<IReadOnlyDictionary<Guid, OverviewStatus>> ReadManyAsync(IReadOnlyList<Guid> serviceIds, CancellationToken token)
    {
        if (serviceIds.Count > 250 || serviceIds.Any(id => id.Version != 7) || serviceIds.Distinct().Count() != serviceIds.Count)
            throw new ArgumentException("Invalid services.", nameof(serviceIds));
        if (serviceIds.Count == 0) return new Dictionary<Guid, OverviewStatus>();
        await using var db = await contexts.CreateDbContextAsync(token);
        var ids = serviceIds.ToArray();
        var existing = await (from service in db.Services.AsNoTracking()
                              join source in db.Sources.AsNoTracking() on service.SourceId equals source.Id
                              where ids.Contains(service.Id)
                              select new { service.Id, CollectionStopped = service.RevokedAt != null, SourceRevoked = source.RevokedAt != null }).ToArrayAsync(token);
        var configured = await db.HealthTargets.AsNoTracking().Where(x => ids.Contains(x.ServiceId))
            .Select(x => x.ServiceId).Distinct().ToArrayAsync(token);

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
                          where ids.Contains(target.ServiceId) && target.Enabled
                          select new HealthRow(target.ServiceId, target.IntervalSeconds, target.TimeoutSeconds, target.Version,
                              service.Version, service.RevokedAt, source.Version, source.RevokedAt,
                              target.InstanceId, instance == null ? null : instance.Version,
                              instance == null ? null : instance.RetiredAt,
                              policy == null ? null : policy.Version, schedule)).ToArrayAsync(token);
        var now = await DiscoveryTransaction.NowAsync(db, token);
        var lookup = rows.ToLookup(row => row.ServiceId);
        var configuredSet = configured.ToHashSet();
        return existing.ToDictionary(x => x.Id, x => Evaluate(lookup[x.Id].ToArray(), configuredSet.Contains(x.Id), now, x.CollectionStopped, x.SourceRevoked));
    }

    private static OverviewStatus Evaluate(HealthRow[] rows, bool configured, DateTimeOffset now, bool collectionStopped, bool sourceRevoked)
    {
        var states = new EvaluatedHealth[rows.Length];
        var reasons = ServiceHealthUnknownReason.None;
        if (rows.Length == 0)
            reasons = configured ? ServiceHealthUnknownReason.Disabled : ServiceHealthUnknownReason.NotConfigured;
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var schedule = row.Schedule;
            if (schedule is null || !schedule.Enabled || row.ServiceRevoked is not null || row.SourceRevoked is not null ||
                schedule.LastEvaluatedAt > now || schedule.EvidenceAt > now ||
                (row.InstanceId is not null && (row.InstanceVersion is null || row.InstanceRetired is not null)) ||
                !Same(schedule.SourceVersion, row.SourceVersion) || !Same(schedule.ServiceVersion, row.ServiceVersion) ||
                !Same(schedule.TargetVersion, row.TargetVersion) || !Same(schedule.InstanceVersion, row.InstanceVersion) ||
                row.PolicyVersion is null || !Same(schedule.NetworkVersion, row.PolicyVersion))
            {
                reasons |= schedule is null ? ServiceHealthUnknownReason.Waiting : ServiceHealthUnknownReason.Invalidated;
                continue;
            }
            var state = new HealthEvaluationState((EvaluatedHealth)schedule.State, schedule.EvidenceAt,
                (HealthStreak)schedule.Streak, schedule.StreakCount, schedule.StreakAt, schedule.LastEvaluatedAt);
            var expired = HealthEvaluation.Expire(state, now, row.IntervalSeconds, row.TimeoutSeconds);
            states[i] = expired.State.State;
            if (expired.State.State == EvaluatedHealth.Unknown)
            {
                var freshness = TimeSpan.FromSeconds(2 * row.IntervalSeconds + row.TimeoutSeconds);
                reasons |= expired.ExpiredAt is not null || schedule.LastEvaluatedAt is { } last && last + freshness <= now
                    ? ServiceHealthUnknownReason.Expired : ServiceHealthUnknownReason.Waiting;
            }
        }
        var aggregate = HealthAggregation.Combine(states);
        return new(new((ServiceHealthState)aggregate.State, aggregate.Participants, aggregate.Unknown, reasons),
            rows.Max(x => x.Schedule?.LastEvaluatedAt), collectionStopped, sourceRevoked);
    }

    private sealed record HealthRow(Guid ServiceId, int IntervalSeconds, int TimeoutSeconds, byte[] TargetVersion,
        byte[] ServiceVersion, DateTimeOffset? ServiceRevoked, byte[] SourceVersion, DateTimeOffset? SourceRevoked,
        Guid? InstanceId, byte[]? InstanceVersion, DateTimeOffset? InstanceRetired, byte[]? PolicyVersion, HealthSchedule? Schedule);

    private static bool Same(byte[]? left, byte[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
}
