using Vigiloom.Contracts.Services;
using OtlpLog = OpenTelemetry.Proto.Logs.V1.LogRecord;
using OtlpSpan = OpenTelemetry.Proto.Trace.V1.Span;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

/// <summary>Produces readable, bounded evidence from the original retained OTLP payload.</summary>
public sealed class ServiceEvidenceReader(TelemetryHistory history)
{
    public async Task<ServiceEvidencePage<ServiceLogEvidence>> LogsAsync(Guid serviceId, DateTimeOffset from, DateTimeOffset to, int page, string? traceId, CancellationToken token)
    {
        byte[]? traceBytes = null;
        if (traceId is not null)
        {
            if (traceId.Length != 32) throw new ArgumentException("Invalid trace identifier.", nameof(traceId));
            try { traceBytes = Convert.FromHexString(traceId); }
            catch (FormatException) { throw new ArgumentException("Invalid trace identifier.", nameof(traceId)); }
        }
        var result = await history.LogsAsync(serviceId, from, to, page, traceBytes, token);
        var items = new List<ServiceLogEvidence>(result.Items.Count);
        foreach (var item in result.Items)
        {
            var log = OtlpLog.Parser.ParseFrom(item.Record);
            var (text, truncated) = LogBodyFormatter.Format(log.Body);
            items.Add(new(item.OccurredAt, item.ReceivedAt, item.UsedReceivedTime, item.UsedObservedTime, item.InstanceId, log.SeverityText,
                text, truncated, item.TraceId, item.SpanId));
        }
        return new(items, result.HasMore, result.Truncated);
    }

    public async Task<ServiceEvidencePage<ServiceTraceEvidence>> TracesAsync(Guid serviceId, DateTimeOffset from, DateTimeOffset to, int page, CancellationToken token)
    {
        var result = await history.TracesAsync(serviceId, from, to, page, null, token);
        var items = new List<ServiceTraceEvidence>(result.Items.Count);
        foreach (var item in result.Items)
        {
            var span = OtlpSpan.Parser.ParseFrom(item.Span);
            items.Add(new(item.StartedAt, item.EndedAt, item.ReceivedAt, item.InstanceId,
                span.Name, span.Status?.Code.ToString() ?? "Unset", item.TraceId, item.SpanId, item.ParentSpanId));
        }
        return new(items, result.HasMore, result.Truncated);
    }
}
