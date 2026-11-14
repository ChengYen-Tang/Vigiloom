using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Web.Publishing;

/// <summary>Reads daily summaries only for a service with explicit, current history authorization.</summary>
internal sealed class PublicServiceHistoryReader(IDbContextFactory<ServiceObservabilityDbContext> contexts, HealthHistoryReader history)
{
    public static bool Valid(DateTimeOffset from, DateTimeOffset to) => from.Offset == TimeSpan.Zero && to.Offset == TimeSpan.Zero &&
        from < to && to - from <= TimeSpan.FromDays(90);

    public async Task<PublicServiceHistory?> ReadAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken token)
    {
        if (id.Version != 7) return null;
        if (!Valid(from, to)) throw new ArgumentException("A UTC history window of at most 90 days is required.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var before = await ReadAuthorizationAsync(db, id, token);
        if (before is null || before.PublicSince == MonitoredService.PendingPublicSince ||
            !PublicationPolicy.TryValidateName(before.Name, out _)) return null;
        var now = await db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        var first = from > before.PublicSince ? from : before.PublicSince;
        var oldest = now.AddDays(-90);
        if (first < oldest) first = oldest;
        var last = to < now ? to : now;
        PublicServiceHistory result;
        if (first >= last) result = new([]);
        else
        {
            var rows = await history.ReadAsync(id, first, last, token);
            if (rows is null) return null;
            result = new(ToDays(first, last, rows.Intervals));
        }
        var after = await ReadAuthorizationAsync(db, id, token);
        return after is not null && before.PublicSince == after.PublicSince && before.Version.AsSpan().SequenceEqual(after.Version) &&
            before.SourceVersion.AsSpan().SequenceEqual(after.SourceVersion) ? result : null;
    }

    private static async Task<Authorization?> ReadAuthorizationAsync(ServiceObservabilityDbContext db, Guid id, CancellationToken token) =>
        await (from service in db.Services.AsNoTracking()
               join source in db.Sources.AsNoTracking() on service.SourceId equals source.Id
               where service.Id == id && service.IsPublic && service.PublicSince != null && service.PublicDisplayName != null &&
                   EF.Functions.Collate(service.PublicPolicyVersion!, "Latin1_General_100_BIN2") == PublicationPolicy.ServiceHistoryVersion &&
                   EF.Functions.DataLength(service.PublicPolicyVersion) == PublicationPolicy.ServiceHistoryVersion.Length * 2
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
