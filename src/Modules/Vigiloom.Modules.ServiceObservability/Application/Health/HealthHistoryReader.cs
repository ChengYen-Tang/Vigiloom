using Microsoft.EntityFrameworkCore;
using System.Runtime.ExceptionServices;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Health;

/// <summary>Builds bounded service history from retained, explicitly configured health intervals.</summary>
public sealed class HealthHistoryReader(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    private const int MaximumIntervals = 20000;

    public async Task<ServiceHealthHistory?> ReadAsync(Guid serviceId, DateTimeOffset startAt, DateTimeOffset endAt, CancellationToken token)
    {
        if (serviceId.Version != 7 || startAt.Offset != TimeSpan.Zero || endAt.Offset != TimeSpan.Zero || startAt >= endAt || endAt - startAt > TimeSpan.FromDays(366))
            throw new HealthHistoryRequestException("Invalid health history request.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var now = await DiscoveryTransaction.NowAsync(db, token);
        if (endAt > now) throw new HealthHistoryRequestException("Health history cannot include the future.");
        if (!await db.Services.AsNoTracking().AnyAsync(x => x.Id == serviceId, token)) return null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = await VersionsAsync(db, serviceId, token);
            ServiceHealthHistory? result = null;
            ExceptionDispatchInfo? failure = null;
            try { result = await ReadSnapshotAsync(db, serviceId, startAt, endAt, token); }
            catch (Exception ex) when (ex is InvalidOperationException or HealthHistoryTooLargeException or ArgumentException)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            var after = await VersionsAsync(db, serviceId, token);
            if (!SameVersions(before, after)) continue;
            failure?.Throw();
            return result;
        }
        throw new HealthHistoryBusyException();
    }

    private static async Task<ServiceHealthHistory> ReadSnapshotAsync(ServiceObservabilityDbContext db, Guid serviceId,
        DateTimeOffset startAt, DateTimeOffset endAt, CancellationToken token)
    {
        var rows = await (from interval in db.HealthStateIntervals.AsNoTracking()
                          join target in db.HealthTargets.AsNoTracking() on interval.TargetId equals target.Id
                          join schedule in db.HealthSchedules.AsNoTracking() on target.Id equals schedule.TargetId into schedules
                          from schedule in schedules.DefaultIfEmpty()
                          where target.ServiceId == serviceId && interval.StartedAt < endAt && (interval.EndedAt == null || interval.EndedAt > startAt)
                          orderby interval.StartedAt, interval.RowId
                          select new { Interval = interval, target.IntervalSeconds, target.TimeoutSeconds, Schedule = schedule })
            .Take(MaximumIntervals + 1).ToArrayAsync(token);
        if (rows.Length > MaximumIntervals) throw new HealthHistoryTooLargeException();
        var policy = await db.HealthNetworkPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.Name == "central", token);
        var targets = await db.HealthTargets.AsNoTracking().Where(x => x.ServiceId == serviceId && x.CreatedAt < endAt)
            .Select(x => new { x.Id, x.CreatedAt }).ToArrayAsync(token);
        var now = await DiscoveryTransaction.NowAsync(db, token);
        var inputs = new List<HealthTimelineInput>(rows.Length + 8);
        var coverage = new Dictionary<Guid, List<(DateTimeOffset Start, DateTimeOffset End)>>();
        foreach (var row in rows)
        {
            var interval = row.Interval;
            var end = interval.EndedAt ?? endAt;
            if (interval.StartedAt >= end) continue;
            if (!coverage.TryGetValue(interval.TargetId, out var spans)) coverage[interval.TargetId] = spans = [];
            spans.Add((interval.StartedAt, end));
            // State 4 is an explicit disabled period, not an Unknown participant.
            if (interval.State == 4) continue;
            var state = (EvaluatedHealth)interval.State;
            var boundary = end;
            if (interval.EndedAt is null && state != EvaluatedHealth.Unknown)
            {
                var schedule = row.Schedule;
                if (schedule is null || schedule.EvidenceAt is null || schedule.LastEvaluatedAt is null ||
                    schedule.EvidenceAt > now || schedule.LastEvaluatedAt > now || policy is null)
                    boundary = interval.StartedAt;
                else
                {
                    var expiry = schedule.EvidenceAt.Value.AddSeconds(2 * row.IntervalSeconds + row.TimeoutSeconds);
                    if (expiry < boundary) boundary = expiry;
                    if (schedule.NetworkVersion is null || !schedule.NetworkVersion.AsSpan().SequenceEqual(policy.Version))
                    {
                        var changed = await HealthStore.PolicyBoundaryAsync(db, interval.TargetId, policy.Id, policy.Generation,
                            schedule.LastEvaluatedAt.Value, token);
                        if (changed < boundary) boundary = changed;
                    }
                    if (boundary < interval.StartedAt) boundary = interval.StartedAt;
                }
            }
            if (boundary > interval.StartedAt) inputs.Add(new(interval.TargetId, state, interval.StartedAt, boundary));
            if (boundary < end) inputs.Add(new(interval.TargetId, EvaluatedHealth.Unknown, boundary, end));
        }
        foreach (var target in targets)
        {
            var cursor = target.CreatedAt > startAt ? target.CreatedAt : startAt;
            if (coverage.TryGetValue(target.Id, out var spans))
            {
                foreach (var span in spans)
                {
                    var gapEnd = span.Start < endAt ? span.Start : endAt;
                    if (cursor < gapEnd) inputs.Add(new(target.Id, EvaluatedHealth.Unknown, cursor, gapEnd));
                    if (span.End > cursor) cursor = span.End;
                }
            }
            if (cursor < endAt) inputs.Add(new(target.Id, EvaluatedHealth.Unknown, cursor, endAt));
        }
        var history = HealthTimeline.Aggregate(startAt, endAt, inputs);
        return new(history.Segments.Select(x => new ServiceHealthInterval(x.Start, x.End, (ServiceHealthState)x.State, x.UnknownParticipants)).ToArray(),
            history.Availability, history.Coverage);
    }

    private sealed record VersionEntry(Guid TargetId, byte[] TargetVersion, byte[]? ScheduleVersion);
    private sealed record VersionSnapshot(VersionEntry[] Entries, byte[]? PolicyVersion);

    private static async Task<VersionSnapshot> VersionsAsync(ServiceObservabilityDbContext db, Guid serviceId, CancellationToken token)
    {
        var targets = await (from target in db.HealthTargets.AsNoTracking()
                             join schedule in db.HealthSchedules.AsNoTracking() on target.Id equals schedule.TargetId into schedules
                             from schedule in schedules.DefaultIfEmpty()
                             where target.ServiceId == serviceId
                             orderby target.RowId
                             select new VersionEntry(target.Id, target.Version, schedule == null ? null : schedule.Version)).ToArrayAsync(token);
        var policy = await db.HealthNetworkPolicies.AsNoTracking().Where(x => x.Name == "central")
            .Select(x => x.Version).SingleOrDefaultAsync(token);
        return new(targets, policy);
    }

    private static bool SameVersions(VersionSnapshot first, VersionSnapshot second)
    {
        if (!Same(first.PolicyVersion, second.PolicyVersion) || first.Entries.Length != second.Entries.Length) return false;
        for (var i = 0; i < first.Entries.Length; i++)
        {
            var a = first.Entries[i]; var b = second.Entries[i];
            if (a.TargetId != b.TargetId || !Same(a.TargetVersion, b.TargetVersion) || !Same(a.ScheduleVersion, b.ScheduleVersion)) return false;
        }
        return true;
    }

    private static bool Same(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
}

/// <summary>The requested history exceeds the bounded query size and should be queried in shorter windows.</summary>
public sealed class HealthHistoryTooLargeException() : Exception("The health history window contains too many intervals.");

/// <summary>Concurrent changes prevented a consistent history read; the caller may retry.</summary>
public sealed class HealthHistoryBusyException() : Exception("Health history changed while it was being read.");

/// <summary>The requested service identifier or UTC history window is invalid.</summary>
public sealed class HealthHistoryRequestException(string message) : ArgumentException(message);
