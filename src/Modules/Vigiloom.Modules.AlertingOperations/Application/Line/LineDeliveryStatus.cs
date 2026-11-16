using System.Data;
using Microsoft.EntityFrameworkCore;
using Vigiloom.Modules.AlertingOperations.Persistence;

namespace Vigiloom.Modules.AlertingOperations.Application.Line;

public sealed record LineDeliveryView(Guid Id, string Status, string? FailureCode, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);
public sealed record LineDeliverySummary(Guid Id, string GroupName, string Status, string? FailureCode, DateTimeOffset CreatedAt);

public sealed class LineDeliveryStatus(IDbContextFactory<OperationsDbContext> contexts)
{
    public async Task<IReadOnlyList<LineDeliverySummary>> GetRecentAsync(CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        await db.Database.OpenConnectionAsync(token);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT TOP (20) d.Id, g.Name, d.CreatedAt, d.CompletedAt, d.FailureCode, o.State, o.LastFailureCode
            FROM operations.LineDeliveries d
            LEFT JOIN operations.LineGroups g ON g.Id = d.GroupId
            LEFT JOIN scheduling.Outbox o ON o.MessageId = d.Id
            ORDER BY d.RowId DESC
            """;
        await using var reader = await command.ExecuteReaderAsync(token);
        var result = new List<LineDeliverySummary>();
        while (await reader.ReadAsync(token))
        {
            var completed = !reader.IsDBNull(3);
            var failure = reader.IsDBNull(4) ? null : reader.GetString(4);
            var state = reader.IsDBNull(5) ? -1 : Convert.ToInt32(reader.GetValue(5));
            var outboxFailure = reader.IsDBNull(6) ? null : reader.GetString(6);
            var status = completed ? failure is null ? "accepted" : "failed" : state == 3 ? "failed" : state == -1 ? "unknown" : "pending";
            result.Add(new(reader.GetGuid(0), reader.IsDBNull(1) ? "未命名群組" : reader.GetString(1), status,
                failure ?? (state == 3 ? outboxFailure : null), reader.GetFieldValue<DateTimeOffset>(2)));
        }
        return result;
    }

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
