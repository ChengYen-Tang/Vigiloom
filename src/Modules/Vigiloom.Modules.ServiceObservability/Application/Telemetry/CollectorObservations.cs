using System.Text;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

/// <summary>Reads evidence for an explicitly selected service; source ownership must be verified by the administrator.</summary>
public sealed class CollectorObservations(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    private static readonly byte[] UptimeName = Encoding.Unicode.GetBytes("otelcol_process_uptime");
    private const int PageSize = 50;

    public async Task<CollectorObservationPage?> QueryAsync(Guid serviceId, int page, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, 2_000);
        await using var db = await contexts.CreateDbContextAsync(token);
        var service = await (from s in db.Services.AsNoTracking()
                             join source in db.Sources.AsNoTracking() on s.SourceId equals source.Id
                             where s.Id == serviceId
                             select new { s.Id, s.SourceId, s.ApprovedAt, s.RevokedAt, SourceRevokedAt = source.RevokedAt }).SingleOrDefaultAsync(token);
        if (service is null) return null;
        // Reception timestamps come from SQL Server, so use the same clock for the query window.
        var through = await DiscoveryTransaction.NowAsync(db, token);
        var since = through.AddDays(-1);
        var rows = await db.Instances.AsNoTracking().Where(x => x.ServiceId == serviceId).OrderBy(x => x.RowId)
            .Skip((page - 1) * PageSize).Take(PageSize + 1).Select(instance => new
            {
                instance.Id, instance.ExternalInstanceId, instance.RetiredAt,
                Latest = db.MetricPoints.Where(point => point.ServiceId == serviceId && point.InstanceId == instance.Id &&
                        point.ReceivedAt >= since && point.ReceivedAt >= service.ApprovedAt && point.ReceivedAt <= through && point.OccurredAt >= service.ApprovedAt &&
                        point.Kind == 2 && point.Context.MetricNameBytes == UptimeName)
                    .OrderByDescending(point => point.OccurredAt).ThenByDescending(point => point.ReceivedAt).ThenByDescending(point => point.RowId)
                    .Select(point => new { point.ReceivedAt, point.OccurredAt }).FirstOrDefault()
            }).ToArrayAsync(token);
        DateTimeOffset? unidentified = null;
        if (page == 1)
            unidentified = await db.MetricPoints.Where(point => point.ServiceId == serviceId && point.InstanceId == null &&
                    point.ReceivedAt >= since && point.ReceivedAt >= service.ApprovedAt && point.ReceivedAt <= through && point.OccurredAt >= service.ApprovedAt &&
                    point.Kind == 2 && point.Context.MetricNameBytes == UptimeName)
                .OrderByDescending(point => point.OccurredAt).ThenByDescending(point => point.ReceivedAt).ThenByDescending(point => point.RowId)
                .Select(point => (DateTimeOffset?)point.ReceivedAt).FirstOrDefaultAsync(token);
        var items = rows.Take(PageSize).Select(x => new CollectorInstanceObservation(x.Id, x.ExternalInstanceId, x.RetiredAt,
            x.Latest?.ReceivedAt, x.Latest?.OccurredAt)).ToArray();
        return new(service.Id, service.SourceId, service.SourceRevokedAt, service.RevokedAt, through, items, unidentified, rows.Length > PageSize);
    }
}
