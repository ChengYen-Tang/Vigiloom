using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Security.Cryptography;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application;

/// <summary>Applies the resource-owned application state to an existing observation record.</summary>
public sealed class ApplicationStateProjection(IDbContextFactory<ServiceObservabilityDbContext> contexts,
    IEnumerable<IHealthTransitionSink>? transitions = null)
{
    public async Task InvalidatePublicationAsync(Guid id, CancellationToken token, Guid? deliveryId = null, byte[]? payloadHash = null)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        if (deliveryId is { } delivery)
        {
            if (delivery.Version != 7 || payloadHash?.Length != 32) throw new ArgumentException("A work ID and payload hash are required.");
            var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO scheduling.WorkReceipts (MessageId, PayloadHash, Result, CompletedAt)
                SELECT {delivery}, {payloadHash}, NULL, SYSDATETIMEOFFSET()
                WHERE NOT EXISTS (SELECT 1 FROM scheduling.WorkReceipts WITH (UPDLOCK, HOLDLOCK) WHERE MessageId = {delivery})
                """, token);
            if (inserted == 0)
            {
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.Transaction = tx.GetDbTransaction();
                command.CommandText = "SELECT PayloadHash FROM scheduling.WorkReceipts WHERE MessageId = @id";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@id";
                parameter.Value = delivery;
                command.Parameters.Add(parameter);
                var savedHash = await command.ExecuteScalarAsync(token) as byte[];
                if (savedHash is null || !CryptographicOperations.FixedTimeEquals(savedHash, payloadHash))
                    throw new InvalidOperationException("The delivery ID is already associated with different work.");
                return;
            }
        }
        if (await HealthPublicationCoordinator.LockServiceAsync(db, id, token) is null) return;
        await HealthPublicationCoordinator.InvalidateAsync(db, id, await DiscoveryTransaction.NowAsync(db, token), token, transitions);
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
        await HealthPublicationCoordinator.InvalidateAsync(db, service.Id, now, token, transitions);
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
