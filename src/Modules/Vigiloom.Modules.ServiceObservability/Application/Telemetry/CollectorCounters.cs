using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Metrics.V1;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

public sealed record CollectorCounter(string Metric, string Series, string Value, ulong StartedAtUnixNano,
    DateTimeOffset MeasuredAt, DateTimeOffset ReceivedAt);
public sealed record CollectorFailureActivity(string Metric, FailureObservation Observation, ulong? From, ulong? Through);
public sealed record CollectorCounterPage(string Exporter, string Instance, DateTimeOffset Through,
    DateTimeOffset? MonitoringSourceRevokedAt, DateTimeOffset? TargetSourceRevokedAt, DateTimeOffset? ServiceStoppedAt, DateTimeOffset? InstanceRetiredAt,
    IReadOnlyList<CollectorCounter> Counters, IReadOnlyList<CollectorFailureActivity> Failures, bool Incomplete);

/// <summary>Bounded, factual counter observations for an administrator-mapped exporter and one instance.</summary>
public sealed class CollectorCounters(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    private const int MaxRows = 128;
    private const int MaxBytes = 1_048_576;
    private static readonly byte[] SentLogs = Encoding.Unicode.GetBytes("otelcol_exporter_sent_log_records");
    private static readonly byte[] FailedLogs = Encoding.Unicode.GetBytes("otelcol_exporter_send_failed_log_records");
    private static readonly byte[] SentMetrics = Encoding.Unicode.GetBytes("otelcol_exporter_sent_metric_points");
    private static readonly byte[] FailedMetrics = Encoding.Unicode.GetBytes("otelcol_exporter_send_failed_metric_points");
    private static readonly byte[] SentTraces = Encoding.Unicode.GetBytes("otelcol_exporter_sent_spans");
    private static readonly byte[] FailedTraces = Encoding.Unicode.GetBytes("otelcol_exporter_send_failed_spans");

    public async Task<CollectorCounterPage?> QueryAsync(Guid collectorId, Guid sourceId, Guid instanceId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var binding = await (from collector in db.Collectors.AsNoTracking()
                             join route in db.CollectorRoutes.AsNoTracking() on collector.Id equals route.CollectorId
                             join service in db.Services.AsNoTracking() on collector.MonitoringServiceId equals service.Id
                             join source in db.Sources.AsNoTracking() on service.SourceId equals source.Id
                             join target in db.Sources.AsNoTracking() on route.SourceId equals target.Id
                             join instance in db.Instances.AsNoTracking() on service.Id equals instance.ServiceId
                             where collector.Id == collectorId && route.SourceId == sourceId && instance.Id == instanceId
                             select new { route.Exporter, instance.ExternalInstanceId, instance.RetiredAt,
                                 service.Id, service.ApprovedAt, ServiceStoppedAt = service.RevokedAt, SourceRevokedAt = source.RevokedAt,
                                 TargetRevokedAt = target.RevokedAt })
            .SingleOrDefaultAsync(token);
        if (binding is null) return null;

        var through = await DiscoveryTransaction.NowAsync(db, token);
        var since = through.AddDays(-1);
        // Fetch lengths first, so a large protobuf cannot bypass the aggregate allocation bound.
        var candidates = await db.MetricPoints.AsNoTracking().Where(p => p.ServiceId == binding.Id && p.InstanceId == instanceId &&
                p.ReceivedAt >= since && p.ReceivedAt >= binding.ApprovedAt && p.ReceivedAt <= through && p.OccurredAt >= binding.ApprovedAt &&
                (p.Context.MetricNameBytes == SentLogs || p.Context.MetricNameBytes == FailedLogs ||
                 p.Context.MetricNameBytes == SentMetrics || p.Context.MetricNameBytes == FailedMetrics ||
                 p.Context.MetricNameBytes == SentTraces || p.Context.MetricNameBytes == FailedTraces))
            .OrderByDescending(p => p.OccurredAt).ThenByDescending(p => p.ReceivedAt).ThenByDescending(p => p.RowId)
            .Select(p => new { p.RowId, Bytes = (long)p.PointPayload.Length + p.Context.MetricPayload.Length + p.Context.ScopePayload.Length +
                p.Context.ResourcePayload.Length + 4L * (p.Context.ResourceSchemaUrl.Length + p.Context.ScopeSchemaUrl.Length) })
            .Take(MaxRows + 1).ToArrayAsync(token);
        var incomplete = candidates.Length > MaxRows;
        var ids = new List<long>(MaxRows);
        long bytes = 0;
        foreach (var candidate in candidates.Take(MaxRows))
        {
            if (candidate.Bytes > MaxBytes - bytes) { incomplete = true; continue; }
            bytes += candidate.Bytes;
            ids.Add(candidate.RowId);
        }
        var rows = await db.MetricPoints.AsNoTracking().Where(p => ids.Contains(p.RowId) && p.ServiceId == binding.Id && p.InstanceId == instanceId)
            .OrderByDescending(p => p.OccurredAt).ThenByDescending(p => p.ReceivedAt).ThenByDescending(p => p.RowId)
            .Select(p => new { p.Kind, p.OccurredAt, p.ReceivedAt, p.PointPayload, p.Context.MetricPayload, p.Context.ScopePayload,
                p.Context.ResourcePayload, p.Context.ResourceSchemaUrl, p.Context.ScopeSchemaUrl })
            .ToArrayAsync(token);
        if (rows.Length != ids.Count) incomplete = true; // retention may run between the two reads
        var samples = new Dictionary<string, CollectorCounter>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            try
            {
                var metric = Metric.Parser.ParseFrom(row.MetricPayload);
                if (row.Kind != 2) { incomplete = true; continue; }
                var point = NumberDataPoint.Parser.ParseFrom(row.PointPayload);
                var exporter = point.Attributes.Where(a => a.Key == "exporter").ToArray();
                if (exporter.Length != 1 || exporter[0].Value?.ValueCase != AnyValue.ValueOneofCase.StringValue) { incomplete = true; continue; }
                if (!StringComparer.Ordinal.Equals(exporter[0].Value.StringValue, binding.Exporter)) continue;
                // Include resource and schema in the identity as well as the full scope and point attributes.
                var identity = Identity(row.ResourcePayload, row.ScopePayload, row.ResourceSchemaUrl, row.ScopeSchemaUrl, point);
                var key = metric.Name + ":" + identity;
                if (!seen.Add(key)) continue;
                if (metric.Sum is not { IsMonotonic: true, AggregationTemporality: AggregationTemporality.Cumulative } ||
                    (point.Flags & (uint)DataPointFlags.NoRecordedValueMask) != 0 || point.TimeUnixNano == 0 || point.ValueCase == NumberDataPoint.ValueOneofCase.None)
                { incomplete = true; continue; }
                var value = point.ValueCase switch
                {
                    NumberDataPoint.ValueOneofCase.AsInt when point.AsInt >= 0 => point.AsInt.ToString(CultureInfo.InvariantCulture),
                    NumberDataPoint.ValueOneofCase.AsDouble when double.IsFinite(point.AsDouble) && point.AsDouble >= 0 => point.AsDouble.ToString("R", CultureInfo.InvariantCulture),
                    _ => null
                };
                if (value is null) { incomplete = true; continue; }
                samples.TryAdd(key, new(metric.Name, identity, value, point.StartTimeUnixNano, row.OccurredAt, row.ReceivedAt));
            }
            catch (InvalidProtocolBufferException) { incomplete = true; }
        }
        var active = binding.SourceRevokedAt is null && binding.TargetRevokedAt is null && binding.ServiceStoppedAt is null && binding.RetiredAt is null;
        var activities = new List<CollectorFailureActivity>(3);
        foreach (var (name, key) in new[] { ("otelcol_exporter_send_failed_log_records", FailedLogs),
                     ("otelcol_exporter_send_failed_metric_points", FailedMetrics), ("otelcol_exporter_send_failed_spans", FailedTraces) })
            activities.Add(await CompareFailureAsync(db, binding.Id, instanceId, binding.Exporter, binding.ApprovedAt, through, active, name, key, token));
        return new(binding.Exporter, binding.ExternalInstanceId, through, binding.SourceRevokedAt, binding.TargetRevokedAt, binding.ServiceStoppedAt,
            binding.RetiredAt, samples.Values.ToArray(), activities, incomplete);
    }

    private static async Task<CollectorFailureActivity> CompareFailureAsync(ServiceObservabilityDbContext db, Guid serviceId, Guid instanceId,
        string exporterName, DateTimeOffset approvedAt, DateTimeOffset through, bool active, string name, byte[] metricName, CancellationToken token)
    {
        var since = through.AddMinutes(-7); // 90-second freshness plus at most five minutes between samples
        // Use the SQL statement's current clock, not the earlier query snapshot: normal concurrent writes
        // are allowed, while receipts left in the future after a server clock rollback prevent inference.
        var futureReceipt = await db.MetricPoints.AsNoTracking().AnyAsync(p => p.ServiceId == serviceId && p.InstanceId == instanceId &&
            p.Context.MetricNameBytes == metricName && p.ReceivedAt >= approvedAt && p.ReceivedAt > DateTimeOffset.UtcNow, token);
        var candidates = await db.MetricPoints.AsNoTracking().Where(p => p.ServiceId == serviceId && p.InstanceId == instanceId &&
                p.ReceivedAt >= since && p.ReceivedAt >= approvedAt && p.ReceivedAt <= through && p.OccurredAt >= approvedAt &&
                p.OccurredAt >= since && p.Context.MetricNameBytes == metricName)
            .OrderByDescending(p => p.OccurredAt).ThenByDescending(p => p.ReceivedAt).ThenByDescending(p => p.RowId)
            .Select(p => new { p.RowId, Bytes = (long)p.PointPayload.Length + p.Context.MetricPayload.Length + p.Context.ScopePayload.Length +
                p.Context.ResourcePayload.Length + 4L * (p.Context.ResourceSchemaUrl.Length + p.Context.ScopeSchemaUrl.Length) })
            .Take(MaxRows + 1).ToArrayAsync(token);
        var incomplete = candidates.Length > MaxRows;
        var ids = new List<long>(MaxRows);
        long bytes = 0;
        foreach (var item in candidates.Take(MaxRows))
        {
            if (item.Bytes > MaxBytes - bytes) { incomplete = true; continue; }
            bytes += item.Bytes; ids.Add(item.RowId);
        }
        var rows = await db.MetricPoints.AsNoTracking().Where(p => ids.Contains(p.RowId) && p.ServiceId == serviceId && p.InstanceId == instanceId)
            .Select(p => new { p.Kind, p.ReceivedAt, p.PointPayload, p.Context.MetricPayload, p.Context.ScopePayload,
                p.Context.ResourcePayload, p.Context.ResourceSchemaUrl, p.Context.ScopeSchemaUrl }).ToArrayAsync(token);
        if (rows.Length != ids.Count) incomplete = true;
        var series = new Dictionary<string, List<FailureSample>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            try
            {
                if (row.Kind != 2) { incomplete = true; continue; }
                var metric = Metric.Parser.ParseFrom(row.MetricPayload);
                var point = NumberDataPoint.Parser.ParseFrom(row.PointPayload);
                var exporter = point.Attributes.Where(a => a.Key == "exporter").ToArray();
                if (exporter.Length != 1 || exporter[0].Value?.ValueCase != AnyValue.ValueOneofCase.StringValue) { incomplete = true; continue; }
                if (!StringComparer.Ordinal.Equals(exporter[0].Value.StringValue, exporterName)) continue;
                var identity = Identity(row.ResourcePayload, row.ScopePayload, row.ResourceSchemaUrl, row.ScopeSchemaUrl, point);
                if (!series.TryGetValue(identity, out var history)) series.Add(identity, history = []);
                history.Add(new(point.TimeUnixNano, point.StartTimeUnixNano, row.ReceivedAt,
                    metric.Sum is { IsMonotonic: true, AggregationTemporality: AggregationTemporality.Cumulative },
                    (point.Flags & (uint)DataPointFlags.NoRecordedValueMask) != 0,
                    point.ValueCase == NumberDataPoint.ValueOneofCase.AsInt ? point.AsInt : null,
                    point.ValueCase == NumberDataPoint.ValueOneofCase.AsDouble ? point.AsDouble : null));
            }
            catch (InvalidProtocolBufferException) { incomplete = true; }
        }
        var comparison = series.Count == 1
            ? CollectorFailureComparison.Evaluate(series.Values.Single(), through, !incomplete && !futureReceipt, active, TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(5))
            : new FailureComparison(FailureObservation.Unknown, null, null);
        return new(name, comparison.Observation, comparison.From, comparison.Through);
    }

    private static string Identity(byte[] resource, byte[] scope, string resourceSchema, string scopeSchema, NumberDataPoint point)
    {
        var dimensions = point.Attributes.Select(a => Convert.ToBase64String(a.ToByteArray())).Order(StringComparer.Ordinal);
        var text = string.Join("|", Convert.ToBase64String(resource), Convert.ToBase64String(scope),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(resourceSchema)), Convert.ToBase64String(Encoding.UTF8.GetBytes(scopeSchema)), string.Join("|", dimensions));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
