using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OpenTelemetry.Proto.Logs.V1;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Admission = Vigiloom.Infrastructure.LogAdmission.LogAdmission;
using StoredLogRecord = Vigiloom.Modules.ServiceObservability.Domain.Telemetry.LogRecord;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

internal enum LogIngestionStatus { Accepted, Unauthorized, Invalid, BeforeApproval, FutureTimestamp, RetiredInstance, CapacityReached, Busy, RateLimited, BatchTooLarge }
internal sealed record LogIngestionResult(LogIngestionStatus Status, TimeSpan? RetryAfter = null);

/// <summary>Writes one bounded OTLP resource/scope group within one authorized service transaction.</summary>
internal sealed class LogIngestion(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    private const int MaximumRecords = 10_000;
    private const int MaximumContextBytes = 65_536;
    private const int MaximumRecordBytes = 262_144;
    private const int MaximumGroupBytes = 4 * 1024 * 1024;

    internal async Task<LogIngestionResult> WriteAsync(Guid authenticatedSourceId, ResourceLogs resource,
        ScopeLogs scope, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(scope);
        if (authenticatedSourceId.Version != 7 || !ResourceIdentity.TryRead(resource.Resource, out var identity) ||
            identity is null || !ValidGroup(resource, scope)) return new(LogIngestionStatus.Invalid);

        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var connection = db.Database.GetDbConnection();
        var transaction = tx.GetDbTransaction();
        var binding = await ServiceInstanceResolver.ResolveAsync(connection, transaction, authenticatedSourceId,
            identity.ServiceKey, identity.Environment, identity.InstanceId, token, identity.ServiceNamespace);
        if (binding.Status is not (InstanceResolutionStatus.Resolved or InstanceResolutionStatus.Unidentified))
            return new(binding.Status switch
            {
                InstanceResolutionStatus.Unauthorized => LogIngestionStatus.Unauthorized,
                InstanceResolutionStatus.Retired => LogIngestionStatus.RetiredInstance,
                InstanceResolutionStatus.CapacityReached => LogIngestionStatus.CapacityReached,
                _ => LogIngestionStatus.Busy
            });
        var received = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        var stage = Stage(db, resource, scope, binding, received);
        if (stage != LogIngestionStatus.Accepted) return new(stage);
        var admission = await Admission.AdmitAsync(connection, transaction, new(LogSourceKind.Application, authenticatedSourceId), scope.LogRecords.Count, token);
        if (admission.Status != LogAdmissionStatus.Accepted)
            return new(admission.Status switch
            {
                LogAdmissionStatus.RateLimited => LogIngestionStatus.RateLimited,
                LogAdmissionStatus.BatchTooLarge => LogIngestionStatus.BatchTooLarge,
                _ => LogIngestionStatus.Busy
            }, admission.RetryAfter);
        await db.SaveChangesAsync(token);
        await tx.CommitAsync(token);
        return new(LogIngestionStatus.Accepted);
    }

    internal static bool ValidGroup(ResourceLogs resource, ScopeLogs scope)
    {
        if (resource.Resource is null || scope.LogRecords.Count is 0 or > MaximumRecords ||
            resource.SchemaUrl.Length > 2048 || scope.SchemaUrl.Length > 2048 ||
            scope.CalculateSize() > MaximumGroupBytes ||
            resource.Resource.CalculateSize() > MaximumContextBytes || (scope.Scope?.CalculateSize() ?? 0) > MaximumContextBytes)
            return false;
        foreach (var log in scope.LogRecords)
            if (log.CalculateSize() > MaximumRecordBytes || !ValidId(log.TraceId, 16) || !ValidId(log.SpanId, 8) ||
                !TryEventTime(EffectiveTime(log), out _)) return false;
        return true;
    }

    internal static LogIngestionStatus Stage(ServiceObservabilityDbContext db, ResourceLogs resource, ScopeLogs scope,
        InstanceResolution binding, DateTimeOffset received)
    {
        var timing = CheckTimes(scope, binding.Service!.ApprovedAt, received);
        if (timing != LogIngestionStatus.Accepted) return timing;

        var shared = new LogContext
        {
            ServiceId = binding.Service.ServiceId,
            ResourcePayload = resource.Resource.ToByteArray(),
            ScopePayload = scope.Scope?.ToByteArray() ?? [],
            ResourceSchemaUrl = resource.SchemaUrl,
            ScopeSchemaUrl = scope.SchemaUrl
        };
        foreach (var log in scope.LogRecords)
        {
            var eventTime = EffectiveTime(log);
            TryEventTime(eventTime, out var occurred);
            db.LogRecords.Add(new StoredLogRecord
            {
                ServiceId = binding.Service.ServiceId, InstanceId = binding.InstanceId, Context = shared,
                OccurredAt = eventTime == 0 ? received : occurred, ReceivedAt = received,
                UsedReceivedTime = eventTime == 0, UsedObservedTime = log.TimeUnixNano == 0 && log.ObservedTimeUnixNano != 0,
                TraceId = log.TraceId.Length == 0 ? null : log.TraceId.ToByteArray(),
                SpanId = log.SpanId.Length == 0 ? null : log.SpanId.ToByteArray(), RecordPayload = log.ToByteArray()
            });
        }
        return LogIngestionStatus.Accepted;
    }

    internal static LogIngestionStatus CheckTimes(ScopeLogs scope, DateTimeOffset approvedAt, DateTimeOffset received)
    {
        if (received < approvedAt || scope.LogRecords.Any(log => EffectiveTime(log) != 0 && TryEventTime(EffectiveTime(log), out var eventTime) && eventTime < approvedAt))
            return LogIngestionStatus.BeforeApproval;
        if (scope.LogRecords.Any(log => EffectiveTime(log) != 0 && TryEventTime(EffectiveTime(log), out var eventTime) && eventTime - received > TimeSpan.FromMinutes(5)))
            return LogIngestionStatus.FutureTimestamp;
        return LogIngestionStatus.Accepted;
    }

    private static bool ValidId(ByteString value, int length) => value.Length == 0 || value.Length == length && value.Span.IndexOfAnyExcept((byte)0) >= 0;

    private static ulong EffectiveTime(OpenTelemetry.Proto.Logs.V1.LogRecord value) => value.TimeUnixNano != 0 ? value.TimeUnixNano : value.ObservedTimeUnixNano;

    private static bool TryEventTime(ulong unixNanoseconds, out DateTimeOffset timestamp)
    {
        var ticks = unixNanoseconds / 100;
        if (ticks > (ulong)(DateTimeOffset.MaxValue.Ticks - DateTimeOffset.UnixEpoch.Ticks)) { timestamp = default; return false; }
        timestamp = DateTimeOffset.UnixEpoch.AddTicks((long)ticks);
        return true;
    }
}
