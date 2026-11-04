using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OpenTelemetry.Proto.Trace.V1;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

internal enum TraceIngestionStatus { Accepted, Unauthorized, Invalid, BeforeApproval, FutureTimestamp, RetiredInstance, CapacityReached, Busy, RateLimited, BatchTooLarge }

/// <summary>Persists one authorized resource and scope group; transport and trace quota remain separate responsibilities.</summary>
internal sealed class TraceIngestion(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    private const int MaximumGroupBytes = 4 * 1024 * 1024;
    private const int MaximumContextBytes = 65_536;
    private const int MaximumSpanBytes = 262_144;

    internal async Task<TraceIngestionStatus> WriteAsync(Guid authenticatedSourceId, ResourceSpans resource,
        ScopeSpans scope, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(scope);
        if (authenticatedSourceId.Version != 7 || !ResourceIdentity.TryRead(resource.Resource, out var identity) || identity is null || !ValidGroup(resource, scope))
            return TraceIngestionStatus.Invalid;

        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var binding = await ServiceInstanceResolver.ResolveAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), authenticatedSourceId,
            identity.ServiceKey, identity.Environment, identity.InstanceId, token, identity.ServiceNamespace);
        if (binding.Status is not (InstanceResolutionStatus.Resolved or InstanceResolutionStatus.Unidentified))
            return binding.Status switch
            {
                InstanceResolutionStatus.Unauthorized => TraceIngestionStatus.Unauthorized,
                InstanceResolutionStatus.Retired => TraceIngestionStatus.RetiredInstance,
                InstanceResolutionStatus.CapacityReached => TraceIngestionStatus.CapacityReached,
                _ => TraceIngestionStatus.Busy
            };
        var received = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        var timing = CheckTimes(scope, binding.Service!.ApprovedAt, received);
        if (timing != TraceIngestionStatus.Accepted) return timing;
        var admission = await TraceAdmission.AdmitAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(),
            [new(authenticatedSourceId, binding.Service.ServiceId, scope.Spans.Count)], token);
        if (admission.Status != LogAdmissionStatus.Accepted)
            return admission.Status switch
            {
                LogAdmissionStatus.RateLimited => TraceIngestionStatus.RateLimited,
                LogAdmissionStatus.BatchTooLarge => TraceIngestionStatus.BatchTooLarge,
                _ => TraceIngestionStatus.Busy
            };
        Stage(db, resource, scope, binding, received);
        await db.SaveChangesAsync(token);
        await tx.CommitAsync(token);
        return TraceIngestionStatus.Accepted;
    }

    internal static bool ValidGroup(ResourceSpans resource, ScopeSpans scope)
    {
        if (resource.Resource is null || scope.Spans.Count is 0 or > 10_000 || scope.CalculateSize() > MaximumGroupBytes ||
            resource.Resource.CalculateSize() > MaximumContextBytes || (scope.Scope?.CalculateSize() ?? 0) > MaximumContextBytes ||
            resource.SchemaUrl.Length > 2048 || scope.SchemaUrl.Length > 2048)
            return false;
        foreach (var span in scope.Spans)
        {
            if (span.CalculateSize() > MaximumSpanBytes || !ValidId(span.TraceId, 16, false) ||
                !ValidId(span.SpanId, 8, false) || !ValidId(span.ParentSpanId, 8, true) ||
                span.StartTimeUnixNano == 0 || span.EndTimeUnixNano < span.StartTimeUnixNano ||
                !TryTime(span.StartTimeUnixNano, out _) || !TryTime(span.EndTimeUnixNano, out _))
                return false;
        }
        return true;
    }

    internal static TraceIngestionStatus CheckTimes(ScopeSpans scope, DateTimeOffset approvedAt, DateTimeOffset received)
    {
        if (received < approvedAt || scope.Spans.Any(span => TryTime(span.StartTimeUnixNano, out var start) && start < approvedAt))
            return TraceIngestionStatus.BeforeApproval;
        if (scope.Spans.Any(span => TryTime(span.EndTimeUnixNano, out var end) && end - received > TimeSpan.FromMinutes(5)))
            return TraceIngestionStatus.FutureTimestamp;
        return TraceIngestionStatus.Accepted;
    }

    internal static void Stage(ServiceObservabilityDbContext db, ResourceSpans resource, ScopeSpans scope, InstanceResolution binding, DateTimeOffset received)
    {
        var shared = new TraceContext { ServiceId = binding.Service!.ServiceId, ResourcePayload = resource.Resource.ToByteArray(),
            ScopePayload = scope.Scope?.ToByteArray() ?? [], ResourceSchemaUrl = resource.SchemaUrl, ScopeSchemaUrl = scope.SchemaUrl };
        foreach (var span in scope.Spans)
        {
            TryTime(span.StartTimeUnixNano, out var start);
            TryTime(span.EndTimeUnixNano, out var end);
            db.TraceSpans.Add(new TraceSpan { ServiceId = binding.Service.ServiceId, InstanceId = binding.InstanceId, Context = shared,
                StartedAt = start, EndedAt = end, ReceivedAt = received, TraceId = span.TraceId.ToByteArray(),
                SpanId = span.SpanId.ToByteArray(), ParentSpanId = span.ParentSpanId.Length == 0 ? null : span.ParentSpanId.ToByteArray(),
                SpanPayload = span.ToByteArray() });
        }
    }

    private static bool ValidId(ByteString value, int length, bool optional) =>
        optional && value.Length == 0 || value.Length == length && value.Span.IndexOfAnyExcept((byte)0) >= 0;

    private static bool TryTime(ulong nanos, out DateTimeOffset value)
    {
        var ticks = nanos / 100;
        if (ticks > (ulong)(DateTimeOffset.MaxValue.Ticks - DateTimeOffset.UnixEpoch.Ticks)) { value = default; return false; }
        value = DateTimeOffset.UnixEpoch.AddTicks((long)ticks);
        return true;
    }
}
