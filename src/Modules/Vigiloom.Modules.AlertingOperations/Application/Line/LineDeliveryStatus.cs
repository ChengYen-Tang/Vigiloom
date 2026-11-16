using System.Data;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application.Line;

public sealed record LineDeliveryView(Guid Id, string Status, string? FailureCode, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public sealed class LineDeliveryStatus(IDbContextFactory<OperationsDbContext> contexts)
{
    public async Task<LineDeliveryView?> GetAsync(Guid id, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var delivery = await db.LineDeliveries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, token);
        if (delivery is null) return null;
        if (delivery.CompletedAt is not null)
            return new(id, delivery.FailureCode is null ? "accepted" : "failed", delivery.FailureCode, delivery.CreatedAt, delivery.CompletedAt);

        await db.Database.OpenConnectionAsync(token);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT State, LastFailureCode FROM scheduling.Outbox WHERE MessageId = @id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.DbType = DbType.Guid;
        parameter.Value = id;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return new(id, "unknown", null, delivery.CreatedAt, null);
        var state = Convert.ToInt32(reader.GetValue(0));
        var failure = reader.IsDBNull(1) ? null : reader.GetString(1);
        return new(id, state == 3 ? "failed" : "pending", failure, delivery.CreatedAt, null);
    }
}
