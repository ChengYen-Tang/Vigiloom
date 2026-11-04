using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Health;

/// <summary>Reads one bounded page of the latest raw observations, without inferring current health.</summary>
public sealed class HealthObservationReader(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    public async Task<IReadOnlyList<HealthCheckObservation>> LatestAsync(Guid serviceId, int page, CancellationToken token)
    {
        if (serviceId.Version != 7 || page is < 1 or > 1000) throw new ArgumentException("Invalid service or page.");
        await using var db = await contexts.CreateDbContextAsync(token);
        var ids = await db.HealthTargets.AsNoTracking().Where(x => x.ServiceId == serviceId)
            .OrderBy(x => x.RowId).Skip((page - 1) * 50).Take(50).Select(x => x.Id).ToArrayAsync(token);
        if (ids.Length == 0) return [];
        var rows = await db.HealthObservations.AsNoTracking().Where(x => ids.Contains(x.TargetId))
            .GroupBy(x => x.TargetId)
            .Select(group => group.OrderByDescending(x => x.ObservedAt).ThenByDescending(x => x.RowId).First())
            .ToArrayAsync(token);
        return rows.Select(x => new HealthCheckObservation(x.TargetId, (HealthCheckObservationKind)x.Kind, x.ObservedAt, x.ReceivedAt)).ToArray();
    }
}
