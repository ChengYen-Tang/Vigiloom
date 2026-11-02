using System.Data;
using System.Data.Common;
using System.Text.Json;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.SpeedTests;

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
        if (audit.ActorId == Guid.Empty || audit.ResourceId == Guid.Empty || audit.OccurredAt.Offset != TimeSpan.Zero || audit.OccurredAt == default)
            throw new ArgumentException("Invalid audit event metadata.", nameof(audit));

        var (kind, details) = audit.Details switch
        {
            SpeedSettingsChanged change when IsValid(change) => (AuditKinds.SpeedSettingsChangedV1, JsonSerializer.Serialize(change)),
            _ => throw new ArgumentException("Unsupported or invalid audit event details.", nameof(audit))
        };
        if (details.Length > 2048) throw new ArgumentException("Audit event details exceed the supported limit.", nameof(audit));
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

    internal static bool IsValid(SpeedSettingsChanged change) =>
        change.Previous is not null && change.Current is not null &&
        SpeedSettingsLimits.IsValid(change.Current.IntervalHours, change.Current.MaxMiB, change.Current.TimeoutSeconds) &&
        IsVersion(change.PreviousVersion) && IsVersion(change.CurrentVersion);

    private static bool IsVersion(string? value)
    {
        if (value is null || value.Length != 12) return false;
        Span<byte> bytes = stackalloc byte[8];
        return Convert.TryFromBase64String(value, bytes, out var written) && written == bytes.Length;
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
