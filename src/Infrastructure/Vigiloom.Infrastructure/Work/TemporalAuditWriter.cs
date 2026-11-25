using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Scheduling.Outbox;

namespace Vigiloom.Infrastructure.Work;

/// <summary>Enqueues a structured audit event with the source service's transaction.</summary>
public sealed class TemporalAuditWriter(OutboxWriter outbox, IConfiguration configuration) : IOperationAuditWriter<OperationAudit>
{
    public Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken cancellationToken)
    {
        _ = OperationAuditValidation.Encode(audit);
        if (connection is not SqlConnection sql || transaction is not SqlTransaction sqlTransaction)
            throw new InvalidOperationException("A SQL Server transaction is required.");
        var queue = configuration["Work:OperationsQueue"] ?? OperationsWork.Queue;
        var request = new TemporalWorkRequest(queue, new ServiceWork(OperationAuditWork.Kind, JsonSerializer.Serialize(audit)));
        return outbox.WriteAsync(new(Guid.CreateVersion7(), TemporalOutboxHandler.Type, JsonSerializer.Serialize(request)), sql, sqlTransaction, cancellationToken);
    }
}
