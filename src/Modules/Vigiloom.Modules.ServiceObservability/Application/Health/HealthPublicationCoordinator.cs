using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Health;

/// <summary>Coordinates service publication writes with health evidence in the caller's transaction.</summary>
public static class HealthPublicationCoordinator
{
    /// <summary>Locks the source before the service, matching health and collection writers.</summary>
    public static async Task<MonitoredService?> LockServiceAsync(ServiceObservabilityDbContext db, Guid serviceId, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("A publication transaction is required.");
        var sourceId = await db.Services.AsNoTracking().Where(x => x.Id == serviceId).Select(x => (Guid?)x.SourceId).SingleOrDefaultAsync(token);
        if (sourceId is null) return null;
        var source = await db.Sources.FromSqlInterpolated($"SELECT * FROM [service].[Sources] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {sourceId.Value}").SingleOrDefaultAsync(token);
        if (source is null) return null;
        return await db.Services.FromSqlInterpolated($"SELECT * FROM [service].[Services] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {serviceId}").SingleOrDefaultAsync(token);
    }

    /// <summary>Invalidates current health evidence at the database-time publication boundary.</summary>
    /// <remarks>Call <see cref="LockServiceAsync"/> first in the same transaction and retain its source and service locks.</remarks>
    public static Task InvalidateAsync(ServiceObservabilityDbContext db, Guid serviceId, DateTimeOffset at, CancellationToken token)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("A publication transaction is required.");
        return HealthInvalidation.ServiceAsync(db, serviceId, at, token);
    }
}
