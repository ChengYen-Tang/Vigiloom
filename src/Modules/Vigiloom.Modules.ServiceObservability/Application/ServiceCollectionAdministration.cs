using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application;

public enum ServiceCollectionStatus { Updated, NotFound, Conflict, SourceUnavailable }

public sealed class ServiceCollectionAdministration(IDbContextFactory<ServiceObservabilityDbContext> contexts,
    IOperationAuditWriter<OperationAudit> audit)
{
    /// <summary>Stops or explicitly resumes one service without changing its publication setting.</summary>
    public async Task<ServiceCollectionStatus> SetStoppedAsync(Guid serviceId, string version, bool stopped, Guid actor, CancellationToken token)
    {
        if (serviceId.Version != 7 || actor == Guid.Empty) throw new ArgumentException("A service and administrator are required.");
        byte[] expected;
        try { expected = Convert.FromBase64String(version ?? ""); }
        catch (FormatException) { return ServiceCollectionStatus.Conflict; }
        if (expected.Length != 8) return ServiceCollectionStatus.Conflict;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var sourceId = await db.Services.AsNoTracking().Where(x => x.Id == serviceId).Select(x => (Guid?)x.SourceId).SingleOrDefaultAsync(token);
        if (sourceId is null) return ServiceCollectionStatus.NotFound;
        // Reception and source revocation lock source before service; hold both until commit.
        var source = await db.Sources.FromSqlInterpolated($"SELECT * FROM [service].[Sources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId.Value}")
            .SingleOrDefaultAsync(token);
        var service = await db.Services.FromSqlInterpolated($"SELECT * FROM [service].[Services] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {serviceId}")
            .SingleOrDefaultAsync(token);
        if (service is null || source is null) return ServiceCollectionStatus.NotFound;
        if (!service.Version.AsSpan().SequenceEqual(expected) || (service.RevokedAt is not null) == stopped) return ServiceCollectionStatus.Conflict;
        if (!stopped && source.RevokedAt is not null) return ServiceCollectionStatus.SourceUnavailable;
        var now = await DiscoveryTransaction.NowAsync(db, token);
        await HealthInvalidation.ServiceAsync(db, serviceId, now, token);
        if (stopped) service.Revoke(now); else service.Resume(now);
        await db.SaveChangesAsync(token);
        await audit.AppendAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(),
            new OperationAudit(actor, serviceId, now, new ServiceCollectionChanged(stopped ? ServiceCollectionAction.Stopped : ServiceCollectionAction.Resumed)), token);
        await tx.CommitAsync(token);
        return ServiceCollectionStatus.Updated;
    }
}
