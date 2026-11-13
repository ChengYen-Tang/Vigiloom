using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application;

/// <summary>Read-only service identity and instance information, without management credentials.</summary>
public sealed class ServiceDetailsReader(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    private const int PageSize = 50;

    public async Task<ServiceDetails?> ReadAsync(Guid id, CancellationToken token)
    {
        if (id.Version != 7) throw new ArgumentException("Invalid service identifier.", nameof(id));
        await using var db = await contexts.CreateDbContextAsync(token);
        var result = await db.Services.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new ServiceDetails(x.Id, x.DisplayName, x.ServiceKey, x.ServiceNamespace, x.Environment, x.ApprovedAt, x.RevokedAt != null,
                db.Sources.Any(source => source.Id == x.SourceId && source.RevokedAt != null),
                db.LogRecords.Where(log => log.ServiceId == x.Id).Max(log => (DateTimeOffset?)log.ReceivedAt),
                db.MetricPoints.Where(metric => metric.ServiceId == x.Id).Max(metric => (DateTimeOffset?)metric.ReceivedAt),
                db.TraceSpans.Where(span => span.ServiceId == x.Id).Max(span => (DateTimeOffset?)span.ReceivedAt), default))
            .SingleOrDefaultAsync(token);
        return result is null ? null : result with { ServerTime = await DiscoveryTransaction.NowAsync(db, token) };
    }

    public async Task<ServiceInstancePage?> InstancesAsync(Guid id, int page, CancellationToken token)
    {
        if (id.Version != 7 || page is < 1 or > 1000) throw new ArgumentException("Invalid service or page.");
        await using var db = await contexts.CreateDbContextAsync(token);
        if (!await db.Services.AsNoTracking().AnyAsync(x => x.Id == id, token)) return null;
        var rows = await db.Instances.AsNoTracking().Where(x => x.ServiceId == id)
            .OrderBy(x => x.RowId).Skip((page - 1) * PageSize).Take(PageSize + 1)
            .Select(x => new ServiceInstanceDetails(x.Id, x.ExternalInstanceId, x.CreatedAt, x.RetiredAt != null))
            .ToArrayAsync(token);
        return new ServiceInstancePage(rows.Take(PageSize).ToArray(), rows.Length > PageSize);
    }
}
