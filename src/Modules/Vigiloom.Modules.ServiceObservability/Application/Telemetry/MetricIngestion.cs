using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OpenTelemetry.Proto.Metrics.V1;
using Vigiloom.Modules.ServiceObservability.Domain.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

internal enum MetricIngestionStatus { Accepted, Unauthorized, Invalid, BeforeApproval, FutureTimestamp, RetiredInstance, CapacityReached, Busy }

/// <summary>Persists one metric and its bounded data points for one authorized service.</summary>
internal sealed class MetricIngestion(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    private const int MaximumContextBytes = 65_536;
    private const int MaximumPointBytes = 262_144;
    private const int MaximumMetricBytes = 4 * 1024 * 1024;

    internal async Task<MetricIngestionStatus> WriteAsync(Guid authenticatedSourceId, ResourceMetrics resource,
        ScopeMetrics scope, Metric metric, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(metric);
        if (authenticatedSourceId.Version != 7 || !ResourceIdentity.TryRead(resource.Resource, out var identity) || identity is null ||
            !TryPrepare(resource, scope, metric, out var data) || data is null) return MetricIngestionStatus.Invalid;
        await using var db = await contexts.CreateDbContextAsync(token);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var binding = await ServiceInstanceResolver.ResolveAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), authenticatedSourceId,
            identity.ServiceKey, identity.Environment, identity.InstanceId, token, identity.ServiceNamespace);
        if (binding.Status is not (InstanceResolutionStatus.Resolved or InstanceResolutionStatus.Unidentified))
            return binding.Status switch
            {
                InstanceResolutionStatus.Unauthorized => MetricIngestionStatus.Unauthorized,
                InstanceResolutionStatus.Retired => MetricIngestionStatus.RetiredInstance,
                InstanceResolutionStatus.CapacityReached => MetricIngestionStatus.CapacityReached,
                _ => MetricIngestionStatus.Busy
            };
        // ResolveAsync takes this lock for identified instances. The unidentified path
        // must use the same transaction fence as batched reception and rollup cleanup.
        if (binding.Status == InstanceResolutionStatus.Unidentified &&
            !await ServiceInstanceResolver.LockServiceAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), binding.Service!.ServiceId, token))
            return MetricIngestionStatus.Busy;
        var received = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
        var timing = CheckTimes(data, binding.Service!.ApprovedAt, received);
        if (timing != MetricIngestionStatus.Accepted) return timing;
        Stage(db, resource, scope, data, binding, received);
        await db.SaveChangesAsync(token);
        await tx.CommitAsync(token);
        return MetricIngestionStatus.Accepted;
    }

    internal static bool TryPrepare(ResourceMetrics resource, ScopeMetrics scope, Metric metric, out MetricGroup? data)
    {
        data = null;
        if (resource.Resource is null || metric.CalculateSize() > MaximumMetricBytes || resource.Resource.CalculateSize() > MaximumContextBytes ||
            (scope.Scope?.CalculateSize() ?? 0) > MaximumContextBytes || resource.SchemaUrl.Length > 2048 || scope.SchemaUrl.Length > 2048)
            return false;
        var count = metric.DataCase switch
        {
            Metric.DataOneofCase.Gauge => metric.Gauge.DataPoints.Count,
            Metric.DataOneofCase.Sum => metric.Sum.DataPoints.Count,
            Metric.DataOneofCase.Histogram => metric.Histogram.DataPoints.Count,
            Metric.DataOneofCase.ExponentialHistogram => metric.ExponentialHistogram.DataPoints.Count,
            Metric.DataOneofCase.Summary => metric.Summary.DataPoints.Count,
            _ => 0
        };
        if (count is 0 or > 10_000) return false;
        data = Extract(metric);
        if (data is null || data.Points.Count is 0 or > 10_000 || data.Metadata.CalculateSize() > MaximumContextBytes ||
            data.Points.Any(p => p.Message.CalculateSize() > MaximumPointBytes || p.Time == 0 || !TryTime(p.Time, out _) || p.Start > p.Time))
        { data = null; return false; }
        return true;
    }

    internal static MetricIngestionStatus CheckTimes(MetricGroup data, DateTimeOffset approvedAt, DateTimeOffset received)
    {
        if (received < approvedAt || data.Points.Any(point => TryTime(point.Time, out var time) && time < approvedAt))
            return MetricIngestionStatus.BeforeApproval;
        if (data.Points.Any(point => TryTime(point.Time, out var time) && time - received > TimeSpan.FromMinutes(5)))
            return MetricIngestionStatus.FutureTimestamp;
        return MetricIngestionStatus.Accepted;
    }

    internal static void Stage(ServiceObservabilityDbContext db, ResourceMetrics resource, ScopeMetrics scope,
        MetricGroup data, InstanceResolution binding, DateTimeOffset received)
    {
        var shared = new MetricContext { ServiceId = binding.Service!.ServiceId, ResourcePayload = resource.Resource.ToByteArray(),
            ScopePayload = scope.Scope?.ToByteArray() ?? [], MetricPayload = data.Metadata.ToByteArray(),
            MetricName = data.Metadata.Name.Length <= 256 ? data.Metadata.Name : null,
            ResourceSchemaUrl = resource.SchemaUrl, ScopeSchemaUrl = scope.SchemaUrl };
        foreach (var point in data.Points)
        {
            TryTime(point.Time, out var occurred);
            db.MetricPoints.Add(new MetricPoint { ServiceId = binding.Service.ServiceId, InstanceId = binding.InstanceId,
                Context = shared, OccurredAt = occurred, ReceivedAt = received, Kind = data.Kind, PointPayload = point.Message.ToByteArray() });
        }
    }

    private static MetricGroup? Extract(Metric metric)
    {
        var metadata = metric.Clone();
        List<MetricValue> values;
        byte kind;
        switch (metric.DataCase)
        {
            case Metric.DataOneofCase.Gauge:
                kind = 1; values = metric.Gauge.DataPoints.Select(p => new MetricValue(p, p.TimeUnixNano, 0)).ToList(); metadata.Gauge.DataPoints.Clear(); break;
            case Metric.DataOneofCase.Sum:
                kind = 2; values = metric.Sum.DataPoints.Select(p => new MetricValue(p, p.TimeUnixNano, p.StartTimeUnixNano)).ToList(); metadata.Sum.DataPoints.Clear(); break;
            case Metric.DataOneofCase.Histogram:
                kind = 3; values = metric.Histogram.DataPoints.Select(p => new MetricValue(p, p.TimeUnixNano, p.StartTimeUnixNano)).ToList(); metadata.Histogram.DataPoints.Clear(); break;
            case Metric.DataOneofCase.ExponentialHistogram:
                kind = 4; values = metric.ExponentialHistogram.DataPoints.Select(p => new MetricValue(p, p.TimeUnixNano, p.StartTimeUnixNano)).ToList(); metadata.ExponentialHistogram.DataPoints.Clear(); break;
            case Metric.DataOneofCase.Summary:
                kind = 5; values = metric.Summary.DataPoints.Select(p => new MetricValue(p, p.TimeUnixNano, p.StartTimeUnixNano)).ToList(); metadata.Summary.DataPoints.Clear(); break;
            default: return null;
        }
        return new(kind, metadata, values);
    }

    private static bool TryTime(ulong nanos, out DateTimeOffset value)
    {
        var ticks = nanos / 100;
        if (ticks > (ulong)(DateTimeOffset.MaxValue.Ticks - DateTimeOffset.UnixEpoch.Ticks)) { value = default; return false; }
        value = DateTimeOffset.UnixEpoch.AddTicks((long)ticks);
        return true;
    }

    internal sealed record MetricValue(IMessage Message, ulong Time, ulong Start);
    internal sealed record MetricGroup(byte Kind, Metric Metadata, List<MetricValue> Points);
}
