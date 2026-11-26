using System.Data;
using System.Data.Common;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;

namespace Vigiloom.Modules.AlertingOperations.Application;

/// <summary>Writes approved structured audit events within the caller's SQL transaction.</summary>
public sealed class OperationAuditWriter : IOperationAuditWriter<OperationAudit>
{
    public async Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(audit);
        if (connection.State != ConnectionState.Open || !ReferenceEquals(transaction.Connection, connection))
            throw new InvalidOperationException("An open connection and its active transaction are required.");
        var (kind, details) = OperationAuditValidation.Encode(audit);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO operations.AuditEvents (ActorId, ResourceId, OccurredAt, Kind, Details) VALUES (@actor, @resource, @time, @kind, @details)";
        Add(command, "@actor", DbType.Guid, audit.ActorId);
        Add(command, "@resource", DbType.Guid, audit.ResourceId);
        Add(command, "@time", DbType.DateTimeOffset, audit.OccurredAt);
        Add(command, "@kind", DbType.Int16, kind);
        Add(command, "@details", DbType.String, details, 2048);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Add(DbCommand command, string name, DbType type, object value, int size = 0)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        if (size > 0) parameter.Size = size;
        command.Parameters.Add(parameter);
    }
}
