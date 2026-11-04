using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application;

public enum InstanceChangeStatus { Updated, NotFound, Conflict, Busy }

public sealed class ServiceInstanceAdministration(IDbContextFactory<ServiceObservabilityDbContext> contexts,
    TimeProvider clock, IOperationAuditWriter<OperationAudit> audit)
{
    public async Task<IReadOnlyList<ManagedServiceSummary>> ListServicesAsync(int page, CancellationToken token)
    {
        if (page is < 1 or > 1000) throw new ArgumentException("A valid page is required.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await db.Services.AsNoTracking().OrderBy(x => x.RowId).Skip((page - 1) * 50).Take(50)
            .Select(x => new { x.Id, x.DisplayName, x.Environment, x.SourceId, x.RevokedAt, x.Version }).ToArrayAsync(token);
        return rows.Select(x => new ManagedServiceSummary(x.Id, x.DisplayName, x.Environment, x.SourceId, x.RevokedAt, Convert.ToBase64String(x.Version))).ToArray();
    }

    public async Task<IReadOnlyList<ServiceInstanceSummary>> ListAsync(Guid serviceId, int page, CancellationToken token)
    {
        if (serviceId.Version != 7 || page is < 1 or > 1000) throw new ArgumentException("A valid service and page are required.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var rows = await db.Instances.AsNoTracking().Where(x => x.ServiceId == serviceId)
            .OrderBy(x => x.RowId).Skip((page - 1) * 50).Take(50)
            .Select(x => new { x.Id, x.ServiceId, x.ExternalInstanceId, x.CreatedAt, x.RetiredAt, x.Version }).ToArrayAsync(token);
        return rows.Select(x => new ServiceInstanceSummary(x.Id, x.ServiceId, x.ExternalInstanceId,
            x.CreatedAt, x.RetiredAt, Convert.ToBase64String(x.Version))).ToArray();
    }

    public async Task<InstanceChangeStatus> SetRetiredAsync(Guid instanceId, string version, bool retired, Guid actor, CancellationToken token)
    {
        if (instanceId.Version != 7 || actor == Guid.Empty) throw new ArgumentException("An instance and administrator are required.");
        byte[] expected;
        try { expected = Convert.FromBase64String(version ?? ""); }
        catch (FormatException) { return InstanceChangeStatus.Conflict; }
        if (expected.Length != 8) return InstanceChangeStatus.Conflict;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var serviceId = await db.Instances.AsNoTracking().Where(x => x.Id == instanceId).Select(x => (Guid?)x.ServiceId).SingleOrDefaultAsync(token);
        if (serviceId is null) return InstanceChangeStatus.NotFound;
        if (!await ServiceInstanceResolver.LockServiceAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), serviceId.Value, token))
            return InstanceChangeStatus.Busy;
        var now = clock.GetUtcNow().ToUniversalTime();
        int affected;
        try
        {
            affected = await db.Instances.Where(x => x.Id == instanceId && x.Version == expected && (retired ? x.RetiredAt == null : x.RetiredAt != null))
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.RetiredAt, retired ? now : (DateTimeOffset?)null).SetProperty(y => y.UpdatedAt, now), token);
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627) { return InstanceChangeStatus.Conflict; }
        if (affected == 0) return InstanceChangeStatus.Conflict;
        await audit.AppendAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(),
            new OperationAudit(actor, instanceId, now, new ServiceInstanceChanged(retired ? ServiceInstanceAction.Retired : ServiceInstanceAction.Restored, serviceId.Value)), token);
        await tx.CommitAsync(token);
        return InstanceChangeStatus.Updated;
    }
}
