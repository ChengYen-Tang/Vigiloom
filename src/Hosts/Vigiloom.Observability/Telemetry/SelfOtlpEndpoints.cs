using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;

namespace Vigiloom.Observability.Telemetry;

/// <summary>Accepts Vigiloom's own telemetry only on the trusted internal network.</summary>
internal static class SelfOtlpEndpoints
{
    internal const string Prefix = "/internal/v1/self-otlp";
    private const string Rejected = "Some data was invalid or not authorized for collection.";

    internal static void MapSelfOtlpEndpoints(this WebApplication app)
    {
        var group = app.MapGroup(Prefix).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(OtlpBodyReader.MaximumBytes));
        group.MapPost("/v1/logs", async (HttpContext context, SelfSourceProvider source, SelfCandidateObserver candidates,
            IDbContextFactory<ServiceObservabilityDbContext> db, IApplicationLogDelivery logs, CancellationToken token) =>
            await OtlpHttpReceiver.ReceiveAsync(context, ExportLogsServiceRequest.Parser, new TelemetryExports(db, candidates, logs).LogsAsync,
                r => new ExportLogsServiceResponse { PartialSuccess = r.Status == ExportStatus.Partial
                    ? new ExportLogsPartialSuccess { RejectedLogRecords = r.Rejected, ErrorMessage = Rejected } : null }, token,
                internalSource: await source.GetAsync(token)));
        group.MapPost("/v1/metrics", async (HttpContext context, SelfSourceProvider source, SelfCandidateObserver candidates,
            IDbContextFactory<ServiceObservabilityDbContext> db, IApplicationLogDelivery logs, CancellationToken token) =>
            await OtlpHttpReceiver.ReceiveAsync(context, ExportMetricsServiceRequest.Parser, new TelemetryExports(db, candidates, logs).MetricsAsync,
                r => new ExportMetricsServiceResponse { PartialSuccess = r.Status == ExportStatus.Partial
                    ? new ExportMetricsPartialSuccess { RejectedDataPoints = r.Rejected, ErrorMessage = Rejected } : null }, token,
                internalSource: await source.GetAsync(token)));
        group.MapPost("/v1/traces", async (HttpContext context, SelfSourceProvider source, SelfCandidateObserver candidates,
            IDbContextFactory<ServiceObservabilityDbContext> db, IApplicationLogDelivery logs, ITraceTimelineSink timeline, CancellationToken token) =>
            await OtlpHttpReceiver.ReceiveAsync(context, ExportTraceServiceRequest.Parser, new TelemetryExports(db, candidates, logs, timeline).TracesAsync,
                r => new ExportTraceServiceResponse { PartialSuccess = r.Status == ExportStatus.Partial
                    ? new ExportTracePartialSuccess { RejectedSpans = r.Rejected, ErrorMessage = Rejected } : null }, token,
                internalSource: await source.GetAsync(token)));
    }
}
