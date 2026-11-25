using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Network;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application;

/// <summary>Applies transitions in source order within the delivery transaction.</summary>
public sealed class PingTransitionReceiver
{
    public async Task ApplyAsync(PingTransitionDelivery delivery, DbConnection connection, DbTransaction transaction, CancellationToken token)
    {
        var transition = delivery.Transition;
        if (transition is null || transition.DeviceId.Version != 7 || string.IsNullOrWhiteSpace(transition.Target) ||
            transition.Target.Length > 253 || transition.Generation <= delivery.PreviousTransitionGeneration || delivery.PreviousTransitionGeneration < 0 ||
            transition.OccurredAt == default || transition.Device is null || string.IsNullOrWhiteSpace(transition.Device.Name) ||
            transition.Device.Name.Length > 256 || transition.Device.Groups is null || !Enum.IsDefined(transition.Current))
            throw new ArgumentException("Invalid Ping transition.");
        var options = new DbContextOptionsBuilder<OperationsDbContext>().UseSqlServer(connection,
            sql => sql.UseCompatibilityLevel(SqlServerRegistration.CompatibilityLevel).MigrationsHistoryTable("__EFMigrationsHistory", "operations")).Options;
        await using var db = new OperationsDbContext(options);
        await db.Database.UseTransactionAsync(transaction, token);
        var cursor = await db.Set<PingTransitionCursor>().FromSqlInterpolated($"SELECT * FROM operations.PingTransitionCursors WITH (UPDLOCK, HOLDLOCK) WHERE DeviceId = {transition.DeviceId} AND Target = {transition.Target}")
            .SingleOrDefaultAsync(token);
        var current = cursor?.Generation ?? 0;
        if (current >= transition.Generation) return;
        if (current != delivery.PreviousTransitionGeneration)
            throw new InvalidOperationException("An earlier Ping transition has not yet been processed.");
        if (cursor is null)
        {
            cursor = new PingTransitionCursor { DeviceId = transition.DeviceId, Target = transition.Target };
            db.Add(cursor);
        }
        cursor.Generation = transition.Generation;
        await db.SaveChangesAsync(token);
        await new PingAlertSink().RecordAsync(transition, connection, transaction, token);
    }
}
