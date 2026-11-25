using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application;

/// <summary>Applies the resource-owned application state to an existing observation record.</summary>
public sealed class ApplicationStateProjection(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    public async Task InvalidatePublicationAsync(Guid id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        if (await HealthPublicationCoordinator.LockServiceAsync(db, id, token) is null) return;
        await HealthPublicationCoordinator.InvalidateAsync(db, id, await DiscoveryTransaction.NowAsync(db, token), token);
        await db.SaveChangesAsync(token);
        await tx.CommitAsync(token);
    }

    public async Task ApplyAsync(Guid id, Func<Guid, CancellationToken, Task<ManagedServiceSummary?>> loadCurrent, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var service = await HealthPublicationCoordinator.LockServiceAsync(db, id, token);
        if (service is null) return;
        // Read the authoritative state after taking the local lock so an older request cannot
        // restore a snapshot after a newer request has already applied a stop or archive.
        var current = await loadCurrent(id, token) ?? throw new InvalidOperationException("Application not found in the resource service.");
        var archived = current.ArchivedAt is not null;
        var stopped = current.StoppedAt is not null;
        if ((service.ArchivedAt is not null) == archived && (service.RevokedAt is not null) == stopped) return;
        var now = await DiscoveryTransaction.NowAsync(db, token);
        await HealthPublicationCoordinator.InvalidateAsync(db, service.Id, now, token);
        if (archived)
        {
            if (service.ArchivedAt is null) service.Archive(now);
        }
        else
        {
            if (service.ArchivedAt is not null) service.Restore(now);
            if (stopped && service.RevokedAt is null) service.Revoke(now);
            if (!stopped && service.RevokedAt is not null) service.Resume(now);
        }
        await db.SaveChangesAsync(token);
        await tx.CommitAsync(token);
    }
}
