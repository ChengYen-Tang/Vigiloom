using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;

namespace Vigiloom.Web.Telemetry;

internal static class OtlpHttpEndpoints
{
    internal const string Prefix = "/api/v1/otlp";
    private const string Rejected = "Some data was invalid or not authorized for collection.";

    internal static void MapOtlpHttpEndpoints(this WebApplication app)
    {
        var group = app.MapGroup(Prefix).RequireAuthorization("ServiceSource")
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(OtlpBodyReader.MaximumBytes));
        group.MapPost("/v1/logs", (HttpContext context, TelemetryExports exports, ReceptionRejectionDiagnostics diagnostics, CancellationToken token) =>
            OtlpHttpReceiver.ReceiveAsync(context, ExportLogsServiceRequest.Parser, exports.LogsAsync,
                r => new ExportLogsServiceResponse { PartialSuccess = r.Status == ExportStatus.Partial
                    ? new ExportLogsPartialSuccess { RejectedLogRecords = r.Rejected, ErrorMessage = Rejected } : null }, token, diagnostics));
        group.MapPost("/v1/metrics", (HttpContext context, TelemetryExports exports, ReceptionRejectionDiagnostics diagnostics, CancellationToken token) =>
            OtlpHttpReceiver.ReceiveAsync(context, ExportMetricsServiceRequest.Parser, exports.MetricsAsync,
                r => new ExportMetricsServiceResponse { PartialSuccess = r.Status == ExportStatus.Partial
                    ? new ExportMetricsPartialSuccess { RejectedDataPoints = r.Rejected, ErrorMessage = Rejected } : null }, token, diagnostics));
        group.MapPost("/v1/traces", (HttpContext context, TelemetryExports exports, ReceptionRejectionDiagnostics diagnostics, CancellationToken token) =>
            OtlpHttpReceiver.ReceiveAsync(context, ExportTraceServiceRequest.Parser, exports.TracesAsync,
                r => new ExportTraceServiceResponse { PartialSuccess = r.Status == ExportStatus.Partial
                    ? new ExportTracePartialSuccess { RejectedSpans = r.Rejected, ErrorMessage = Rejected } : null }, token, diagnostics));
    }
}
