using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Modules.ServiceObservability.Application.Telemetry;

/// <summary>Transport-neutral OTLP operations; the source ID must come from successful authentication.</summary>
public sealed class TelemetryExports(IDbContextFactory<ServiceObservabilityDbContext> contexts, CandidateDiscovery candidates)
{
    public Task<ExportResult> LogsAsync(AuthenticatedSource source, ExportLogsServiceRequest request, CancellationToken token) =>
        source.Version is { Length: 8 } ? new LogExport(contexts, candidates).WriteAsync(source.SourceId, request, token, source.Version) : Task.FromResult(new ExportResult(ExportStatus.Invalid, 0));

    public Task<ExportResult> TracesAsync(AuthenticatedSource source, ExportTraceServiceRequest request, CancellationToken token) =>
        source.Version is { Length: 8 } ? new TraceExport(contexts, candidates).WriteAsync(source.SourceId, request, token, source.Version) : Task.FromResult(new ExportResult(ExportStatus.Invalid, 0));

    public Task<ExportResult> MetricsAsync(AuthenticatedSource source, ExportMetricsServiceRequest request, CancellationToken token) =>
        source.Version is { Length: 8 } ? new MetricExport(contexts, candidates).WriteAsync(source.SourceId, request, token, source.Version) : Task.FromResult(new ExportResult(ExportStatus.Invalid, 0));
}
