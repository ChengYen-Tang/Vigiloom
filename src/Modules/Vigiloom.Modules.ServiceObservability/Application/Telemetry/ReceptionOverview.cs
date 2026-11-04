using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

/// <summary>Queries the latest persisted signal per source without making a Collector health inference.</summary>
public sealed class ReceptionOverview(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    public async Task<IReadOnlyList<SourceReceptionSummary>> ListAsync(int page, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, 200);
        await using var db = await contexts.CreateDbContextAsync(token);
        var sources = await db.Sources.AsNoTracking().OrderByDescending(x => x.RowId).Skip((page - 1) * 50).Take(50)
            .Select(x => new { x.Id, x.Name, x.RevokedAt }).ToArrayAsync(token);
        if (sources.Length == 0) return [];
        var ids = sources.Select(x => x.Id).ToArray();
        var perService = db.Services.AsNoTracking().Where(service => ids.Contains(service.SourceId)).Select(service => new
        {
            service.SourceId,
            Log = db.LogRecords.Where(x => x.ServiceId == service.Id).OrderByDescending(x => x.ReceivedAt).Select(x => (DateTimeOffset?)x.ReceivedAt).FirstOrDefault(),
            Metric = db.MetricPoints.Where(x => x.ServiceId == service.Id).OrderByDescending(x => x.ReceivedAt).Select(x => (DateTimeOffset?)x.ReceivedAt).FirstOrDefault(),
            Trace = db.TraceSpans.Where(x => x.ServiceId == service.Id).OrderByDescending(x => x.ReceivedAt).Select(x => (DateTimeOffset?)x.ReceivedAt).FirstOrDefault()
        });
        var received = await perService.GroupBy(x => x.SourceId).Select(g => new
        {
            SourceId = g.Key, Log = g.Max(x => x.Log), Metric = g.Max(x => x.Metric), Trace = g.Max(x => x.Trace)
        }).ToDictionaryAsync(x => x.SourceId, token);
        return sources.Select(x => new SourceReceptionSummary(x.Id, x.Name, x.RevokedAt,
            received.TryGetValue(x.Id, out var value) ? value.Log : null,
            value?.Metric, value?.Trace)).ToArray();
    }
}
