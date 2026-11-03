using System.Data;
using System.Data.Common;
using System.Text.Json;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Agent;

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
            PingSettingsChanged change when IsValid(change) => (AuditKinds.PingSettingsChangedV1, JsonSerializer.Serialize(change)),
            AgentReplayPolicyChanged change when IsValid(change) => (AuditKinds.AgentReplayPolicyChangedV1, JsonSerializer.Serialize(change)),
            ServiceApproved change when change.SourceId.Version == 7 => (AuditKinds.ServiceApprovedV1, JsonSerializer.Serialize(change)),
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

    internal static bool IsValid(PingSettingsChanged change) => change.Previous is not null && change.Current is not null &&
        PingSettingsLimits.IsValid(change.Current.IntervalSeconds, change.Current.TimeoutSeconds, change.Current.FailureThreshold,
            change.Current.SuccessThreshold, change.Current.MaxConcurrency) &&
        (change.PreviousVersion == "" || IsVersion(change.PreviousVersion)) && IsVersion(change.CurrentVersion);

    internal static bool IsValid(AgentReplayPolicyChanged change) =>
        change.ActivatedAt.Offset == TimeSpan.Zero && change.GraceUntil.Offset == TimeSpan.Zero &&
        change.GraceUntil - change.ActivatedAt == TimeSpan.FromHours(24) + TimeSpan.FromMinutes(5) &&
        ((change.Previous == AgentReplayPhase.Disabled && change.Current == AgentReplayPhase.Grace && change.EnforcedAt is null) ||
         (change.Previous == AgentReplayPhase.Grace && change.Current == AgentReplayPhase.Enforced &&
          change.EnforcedAt is { Offset: var offset } enforced && offset == TimeSpan.Zero && enforced >= change.GraceUntil)) &&
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
