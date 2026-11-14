using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Web.Publishing;

/// <summary>Reads public history only within the service's explicitly authorized policy.</summary>
internal sealed class PublicServiceHistoryReader(IDbContextFactory<ServiceObservabilityDbContext> contexts, HealthHistoryReader history)
{
    public static bool Valid(DateTimeOffset from, DateTimeOffset to) => from.Offset == TimeSpan.Zero && to.Offset == TimeSpan.Zero &&
        from < to && to - from <= TimeSpan.FromDays(90);
    public static bool ValidPeriods(DateTimeOffset from, DateTimeOffset to) => Valid(from, to) && to - from <= TimeSpan.FromDays(1);

    public Task<PublicServiceHistory?> ReadAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken token) =>
        ReadCoreAsync(id, from, to, false, static (first, last, _, intervals) => new PublicServiceHistory(ToDays(first, last, intervals)), token);

    public Task<PublicServiceHealthPeriods?> ReadPeriodsAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken token)
    {
        if (!ValidPeriods(from, to)) throw new ArgumentException("A UTC history window of at most one day is required.");
        return ReadCoreAsync(id, from, to, true, static (_, _, now, intervals) => new PublicServiceHealthPeriods(ToPeriods(intervals), now), token);
    }

    private async Task<T?> ReadCoreAsync<T>(Guid id, DateTimeOffset from, DateTimeOffset to, bool requireDetails,
        Func<DateTimeOffset, DateTimeOffset, DateTimeOffset, IReadOnlyList<ServiceHealthInterval>, T> project, CancellationToken token) where T : class
    {
        if (id.Version != 7) return null;
        if (!Valid(from, to)) throw new ArgumentException("A UTC history window of at most 90 days is required.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var before = await ReadAuthorizationAsync(db, id, requireDetails, token);
        if (before is null || before.PublicSince == MonitoredService.PendingPublicSince ||
            !PublicationPolicy.TryValidateName(before.Name, out _)) return null;
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        var first = from > before.PublicSince ? from : before.PublicSince;
        var oldest = now.AddDays(-90);
        if (first < oldest) first = oldest;
        var last = to < now ? to : now;
        T result;
        if (first >= last) result = project(last, last, now, []);
        else
        {
            var rows = await history.ReadAsync(id, first, last, token);
            if (rows is null) return null;
            result = project(first, last, now, rows.Intervals);
        }
        var after = await ReadAuthorizationAsync(db, id, requireDetails, token);
        return after is not null && before.PublicSince == after.PublicSince && before.Version.AsSpan().SequenceEqual(after.Version) &&
            before.SourceVersion.AsSpan().SequenceEqual(after.SourceVersion) ? result : null;
    }

    internal static IReadOnlyList<PublicHealthPeriod> ToPeriods(IReadOnlyList<ServiceHealthInterval> intervals)
    {
        const int maximumPeriods = 512;
        var result = new List<PublicHealthPeriod>();
        foreach (var interval in intervals)
        {
            var state = interval.State switch
            {
                ServiceHealthState.Healthy => "Healthy",
                ServiceHealthState.Degraded => "Degraded",
                ServiceHealthState.Unhealthy => "Unhealthy",
                _ => "Unknown"
            };
            if (result.Count > 0 && result[^1].To == interval.Start && result[^1].State == state)
            {
                result[^1] = result[^1] with { To = interval.End };
                continue;
            }
            if (result.Count == maximumPeriods) throw new HealthHistoryTooLargeException();
            result.Add(new(interval.Start, interval.End, state, result.Count > 0));
        }
        return result;
    }

    private static async Task<Authorization?> ReadAuthorizationAsync(ServiceObservabilityDbContext db, Guid id, bool requireDetails, CancellationToken token) =>
        await (from service in db.Services.AsNoTracking()
               join source in db.Sources.AsNoTracking() on service.SourceId equals source.Id
               where service.Id == id && service.IsPublic && service.PublicSince != null && service.PublicDisplayName != null &&
                   ((EF.Functions.Collate(service.PublicPolicyVersion!, "Latin1_General_100_BIN2") == PublicationPolicy.ServiceHistoryDetailsVersion &&
                     EF.Functions.DataLength(service.PublicPolicyVersion) == PublicationPolicy.ServiceHistoryDetailsVersion.Length * 2) ||
                    (!requireDetails && EF.Functions.Collate(service.PublicPolicyVersion!, "Latin1_General_100_BIN2") == PublicationPolicy.ServiceHistoryVersion &&
                     EF.Functions.DataLength(service.PublicPolicyVersion) == PublicationPolicy.ServiceHistoryVersion.Length * 2))
               select new Authorization(service.PublicDisplayName!, service.PublicSince!.Value, service.Version, source.Version))
            .SingleOrDefaultAsync(token);

    internal static IReadOnlyList<PublicHealthDay> ToDays(DateTimeOffset from, DateTimeOffset to, IReadOnlyList<ServiceHealthInterval> intervals)
    {
        var days = new List<PublicHealthDay>(91);
        var position = 0;
        for (var start = from; start < to;)
        {
            var nextMidnight = new DateTimeOffset(start.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
            var end = nextMidnight < to ? nextMidnight : to;
            long healthy = 0, degraded = 0, unhealthy = 0, unknown = 0;
            while (position < intervals.Count && intervals[position].End <= start) position++;
            for (var i = position; i < intervals.Count && intervals[i].Start < end; i++)
            {
                var item = intervals[i];
                var segmentStart = item.Start > start ? item.Start : start;
                var segmentEnd = item.End < end ? item.End : end;
                if (segmentEnd <= segmentStart) continue;
                var ticks = (segmentEnd - segmentStart).Ticks;
                switch (item.State)
                {
                    case ServiceHealthState.Healthy: healthy += ticks; break;
                    case ServiceHealthState.Degraded: degraded += ticks; break;
                    case ServiceHealthState.Unhealthy: unhealthy += ticks; break;
                    default: unknown += ticks; break;
                }
            }
            var total = (end - start).Ticks;
            var known = healthy + degraded + unhealthy;
            // Missing intervals are unknown, not healthy or downtime.
            if (known + unknown < total) unknown = total - known;
            var state = unhealthy > 0 ? "Unhealthy" : degraded > 0 ? "Degraded" : unknown > 0 || known == 0 ? "Unknown" : "Healthy";
            days.Add(new(start, end, state, known == 0 ? null : healthy / (double)known, known / (double)total));
            start = end;
        }
        return days;
    }

    private sealed record Authorization(string Name, DateTimeOffset PublicSince, byte[] Version, byte[] SourceVersion);
}
