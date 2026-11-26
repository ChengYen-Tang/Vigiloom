using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Resources;
using Vigiloom.Contracts.Services;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application;

/// <summary>Applies service health transitions in source order.</summary>
public sealed class HealthTransitionReceiver
{
    public async Task ApplyAsync(HealthTransitionDelivery delivery, DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        var transition = delivery.Transition;
        if (transition is null || transition.TargetId.Version != 7 || transition.ServiceId.Version != 7 ||
            transition.InstanceId is { Version: not 7 } || transition.Generation <= delivery.PreviousTransitionGeneration ||
            delivery.PreviousTransitionGeneration < 0 || transition.ObservedAt == default || !Enum.IsDefined(transition.State) ||
            string.IsNullOrWhiteSpace(transition.ServiceName) || transition.Environment is null || delivery.Groups is null)
            throw new ArgumentException("Invalid health transition.");
        var options = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(connection,
            sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel).MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
        await using var db = new OperationsDbContext(options);
        await db.Database.UseTransactionAsync(transaction, token);
        var cursor = await db.Set<HealthTransitionCursor>().FromSqlInterpolated($"SELECT * FROM operations.HealthTransitionCursors WITH (UPDLOCK, HOLDLOCK) WHERE TargetId = {transition.TargetId}")
            .SingleOrDefaultAsync(token);
        var current = cursor?.Generation ?? 0;
        if (current >= transition.Generation) return;
        if (current != delivery.PreviousTransitionGeneration)
            throw new InvalidOperationException("An earlier health transition has not yet been processed.");
        if (cursor is null) { cursor = new HealthTransitionCursor { TargetId = transition.TargetId }; db.Add(cursor); }
        cursor.Generation = transition.Generation;
        await db.SaveChangesAsync(token);
        await new ServiceHealthAlertSink(new Groups(delivery.Groups)).RecordAsync(transition, connection, transaction, token);
    }

    private sealed class Groups(IReadOnlyList<ResourceGroupSnapshot> groups) : IServiceGroupSource
    {
        public Task<IReadOnlyList<ResourceGroupSnapshot>> ReadAsync(Guid serviceId, DbConnection connection, DbTransaction transaction, CancellationToken token) => Task.FromResult(groups);
    }
}
