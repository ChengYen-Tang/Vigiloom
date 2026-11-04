using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application;

public enum CollectorChangeStatus { Updated, NotFound, Conflict, SourceUnavailable, LimitReached }
public sealed record CollectorChangeResult(CollectorChangeStatus Status, Guid? CollectorId = null);

/// <summary>Administrator-managed Collector identity and source mappings; mutations and audit commit together.</summary>
public sealed class CollectorAdministration(IDbContextFactory<ServiceObservabilityDbContext> contexts, IOperationAuditWriter<OperationAudit> audit)
{
    public async Task<CollectorSummary?> GetAsync(Guid id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var row = await (from collector in db.Collectors.AsNoTracking()
                         join service in db.Services.AsNoTracking() on collector.MonitoringServiceId equals service.Id
                         join source in db.Sources.AsNoTracking() on service.SourceId equals source.Id
                         where collector.Id == id
                         select new { collector.Id, collector.Name, collector.MonitoringServiceId, service.SourceId,
                             SourceRevokedAt = source.RevokedAt, ServiceStoppedAt = service.RevokedAt, collector.Version }).SingleOrDefaultAsync(token);
        return row is null ? null : new(row.Id, row.Name, row.MonitoringServiceId, row.SourceId,
            row.SourceRevokedAt, row.ServiceStoppedAt, Convert.ToBase64String(row.Version));
    }

    public async Task<IReadOnlyList<CollectorSummary>> ListAsync(int page, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, 2_000);
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await (from collector in db.Collectors.AsNoTracking()
                          join service in db.Services.AsNoTracking() on collector.MonitoringServiceId equals service.Id
                          join source in db.Sources.AsNoTracking() on service.SourceId equals source.Id
                          orderby collector.RowId
                          select new { collector.Id, collector.Name, collector.MonitoringServiceId, service.SourceId,
                              SourceRevokedAt = source.RevokedAt, ServiceStoppedAt = service.RevokedAt, collector.Version })
            .Skip((page - 1) * 50).Take(50).ToArrayAsync(token);
        return rows.Select(x => new CollectorSummary(x.Id, x.Name, x.MonitoringServiceId, x.SourceId,
            x.SourceRevokedAt, x.ServiceStoppedAt, Convert.ToBase64String(x.Version))).ToArray();
    }

    public async Task<IReadOnlyList<CollectorRouteSummary>> RoutesAsync(Guid collectorId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await (from route in db.CollectorRoutes.AsNoTracking()
                      join source in db.Sources.AsNoTracking() on route.SourceId equals source.Id
                      where route.CollectorId == collectorId
                      orderby source.RowId
                      select new CollectorRouteSummary(source.Id, source.Name, source.RevokedAt, route.Exporter))
            .Take(100).ToArrayAsync(token);
    }

    public async Task<CollectorChangeResult> RegisterAsync(string name, Guid serviceId, bool dedicatedSourceConfirmed, Guid actor, CancellationToken token)
    {
        if (!dedicatedSourceConfirmed || actor == Guid.Empty || serviceId.Version != 7 || string.IsNullOrWhiteSpace(name) || name.Length > 128)
            throw new ArgumentException("A name, monitoring service, confirmation and administrator are required.");
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var sourceId = await db.Services.AsNoTracking().Where(x => x.Id == serviceId).Select(x => (Guid?)x.SourceId).SingleOrDefaultAsync(token);
        if (sourceId is null) return new(CollectorChangeStatus.NotFound);
        var source = await db.Sources.FromSqlInterpolated($"SELECT * FROM [service].[Sources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId.Value}").SingleOrDefaultAsync(token);
        var service = await db.Services.FromSqlInterpolated($"SELECT * FROM [service].[Services] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {serviceId}").SingleOrDefaultAsync(token);
        if (source is null || source.RevokedAt is not null || service is null || service.SourceId != source.Id || service.RevokedAt is not null)
            return new(CollectorChangeStatus.SourceUnavailable);
        var now = await DiscoveryTransaction.NowAsync(db, token);
        var collector = new CollectorRegistration(name, service.Id, now);
        db.Collectors.Add(collector);
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return new(CollectorChangeStatus.Conflict); }
        await audit.AppendAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(),
            new OperationAudit(actor, collector.Id, now, new CollectorChanged(CollectorAction.Registered, service.Id, null)), token);
        await tx.CommitAsync(token);
        return new(CollectorChangeStatus.Updated, collector.Id);
    }

    public async Task<CollectorChangeStatus> SetRouteAsync(Guid collectorId, Guid sourceId, string version, string? exporter,
        bool add, Guid actor, CancellationToken token)
    {
        if (collectorId.Version != 7 || sourceId.Version != 7 || actor == Guid.Empty ||
            add && (string.IsNullOrWhiteSpace(exporter) || exporter.Length > 128))
            throw new ArgumentException("Valid identifiers, exporter and administrator are required.");
        byte[] expected;
        try { expected = Convert.FromBase64String(version ?? ""); }
        catch (FormatException) { return CollectorChangeStatus.Conflict; }
        if (expected.Length != 8) return CollectorChangeStatus.Conflict;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var collector = await db.Collectors.FromSqlInterpolated($"SELECT * FROM [service].[Collectors] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {collectorId}").SingleOrDefaultAsync(token);
        if (collector is null) return CollectorChangeStatus.NotFound;
        if (!collector.Version.AsSpan().SequenceEqual(expected)) return CollectorChangeStatus.Conflict;
        var existing = await db.CollectorRoutes.SingleOrDefaultAsync(x => x.CollectorId == collectorId && x.SourceId == sourceId, token);
        if (add)
        {
            if (existing is not null) return CollectorChangeStatus.Conflict;
            var source = await db.Sources.FromSqlInterpolated($"SELECT * FROM [service].[Sources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId}").SingleOrDefaultAsync(token);
            var monitorSource = await db.Services.Where(x => x.Id == collector.MonitoringServiceId).Select(x => x.SourceId).SingleAsync(token);
            if (source is null || source.RevokedAt is not null || source.Id == monitorSource) return CollectorChangeStatus.SourceUnavailable;
            if (await db.CollectorRoutes.CountAsync(x => x.CollectorId == collectorId, token) >= 100) return CollectorChangeStatus.LimitReached;
            db.CollectorRoutes.Add(new CollectorRoute(collectorId, sourceId, exporter!));
        }
        else
        {
            if (existing is null) return CollectorChangeStatus.NotFound;
            db.CollectorRoutes.Remove(existing);
        }
        var now = await DiscoveryTransaction.NowAsync(db, token);
        collector.Touch(now);
        db.Entry(collector).Property(x => x.UpdatedAt).IsModified = true;
        try { await db.SaveChangesAsync(token); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 }) { return CollectorChangeStatus.Conflict; }
        await audit.AppendAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(),
            new OperationAudit(actor, collector.Id, now, new CollectorChanged(add ? CollectorAction.RouteAdded : CollectorAction.RouteRemoved,
                collector.MonitoringServiceId, sourceId)), token);
        await tx.CommitAsync(token);
        return CollectorChangeStatus.Updated;
    }
}
