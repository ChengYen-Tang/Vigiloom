using Microsoft.EntityFrameworkCore;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

/// <summary>Produces bounded transport DTOs without exposing internal database keys.</summary>
public sealed class TelemetryHistory(IDbContextFactory<ServiceObservabilityDbContext> contexts)
{
    public Task<MetricRollupWindowPage> MetricWindowsAsync(Guid serviceId, DateTimeOffset from, DateTimeOffset to,
        DateTimeOffset? before, CancellationToken token) => new MetricRollupHistory(contexts).WindowsAsync(serviceId, from, to, before, token);

    public async Task<TelemetryPage<TelemetryLog, TelemetryContext>> LogsAsync(Guid serviceId, DateTimeOffset from, DateTimeOffset to,
        int page, byte[]? traceId, CancellationToken token)
    {
        var rows = await new LogHistory(contexts).QueryAsync(serviceId, from, to, page, token, traceId);
        var indexes = new Dictionary<long, int>();
        var shared = new List<TelemetryContext>();
        var items = new List<TelemetryLog>(rows.Items.Count);
        foreach (var row in rows.Items)
        {
            if (!indexes.TryGetValue(row.ContextId, out var index))
            {
                index = shared.Count; indexes.Add(row.ContextId, index);
                shared.Add(new(row.Context.ResourcePayload, row.Context.ScopePayload, row.Context.ResourceSchemaUrl, row.Context.ScopeSchemaUrl));
            }
            items.Add(new(row.InstanceId, row.OccurredAt, row.ReceivedAt, row.UsedReceivedTime, row.UsedObservedTime,
                Hex(row.TraceId), Hex(row.SpanId), index, row.RecordPayload));
        }
        return new(items, shared, rows.HasMore, rows.Truncated);
    }

    public async Task<TelemetryPage<TelemetrySpan, TelemetryContext>> TracesAsync(Guid serviceId, DateTimeOffset from, DateTimeOffset to,
        int page, byte[]? traceId, CancellationToken token)
    {
        var rows = await new TraceHistory(contexts).QueryAsync(serviceId, from, to, page, token, traceId);
        var indexes = new Dictionary<long, int>();
        var shared = new List<TelemetryContext>();
        var items = new List<TelemetrySpan>(rows.Items.Count);
        foreach (var row in rows.Items)
        {
            if (!indexes.TryGetValue(row.ContextId, out var index))
            {
                index = shared.Count; indexes.Add(row.ContextId, index);
                shared.Add(new(row.Context.ResourcePayload, row.Context.ScopePayload, row.Context.ResourceSchemaUrl, row.Context.ScopeSchemaUrl));
            }
            items.Add(new(row.InstanceId, row.StartedAt, row.EndedAt, row.ReceivedAt, Hex(row.TraceId)!, Hex(row.SpanId)!,
                Hex(row.ParentSpanId), index, row.SpanPayload));
        }
        return new(items, shared, rows.HasMore, rows.Truncated);
    }

    public async Task<TelemetryPage<TelemetryMetricPoint, MetricTelemetryContext>> MetricsAsync(Guid serviceId, DateTimeOffset from,
        DateTimeOffset to, int page, CancellationToken token)
    {
        var rows = await new MetricHistory(contexts).QueryAsync(serviceId, from, to, page, token);
        var indexes = new Dictionary<long, int>();
        var shared = new List<MetricTelemetryContext>();
        var items = new List<TelemetryMetricPoint>(rows.Items.Count);
        foreach (var row in rows.Items)
        {
            if (!indexes.TryGetValue(row.ContextId, out var index))
            {
                index = shared.Count; indexes.Add(row.ContextId, index);
                shared.Add(new(new(row.Context.ResourcePayload, row.Context.ScopePayload, row.Context.ResourceSchemaUrl, row.Context.ScopeSchemaUrl), row.Context.MetricPayload));
            }
            items.Add(new(row.InstanceId, row.OccurredAt, row.ReceivedAt, row.Kind, index, row.PointPayload));
        }
        return new(items, shared, rows.HasMore, rows.Truncated);
    }

    private static string? Hex(byte[]? bytes) => bytes is null ? null : Convert.ToHexStringLower(bytes);
}
