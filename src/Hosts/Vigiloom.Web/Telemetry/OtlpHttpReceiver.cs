using System.Data.Common;
using System.Security.Claims;
using Google.Protobuf;
using Microsoft.Net.Http.Headers;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Telemetry;

/// <summary>Bounded admission for authenticated OTLP/HTTP requests.</summary>
internal sealed class OtlpConcurrency : IDisposable
{
    internal SemaphoreSlim Gate { get; } = new(4, 4);
    public void Dispose() => Gate.Dispose();
}

internal static class OtlpHttpReceiver
{
    internal static async Task<IResult> ReceiveAsync<TRequest, TResponse>(HttpContext context,
        MessageParser<TRequest> parser, Func<AuthenticatedSource, TRequest, CancellationToken, Task<ExportResult>> export,
        Func<ExportResult, TResponse> response, CancellationToken token)
        where TRequest : class, IMessage<TRequest>
        where TResponse : IMessage
    {
        var source = ServiceSourceCredentialHandler.FromPrincipal(context.User);
        if (source is null)
            return OtlpResponse.Failure(StatusCodes.Status401Unauthorized, "A valid service source credential is required.");
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType) ||
            !string.Equals(contentType.MediaType.Value, OtlpResponse.ContentType, StringComparison.OrdinalIgnoreCase))
            return OtlpResponse.Failure(StatusCodes.Status415UnsupportedMediaType, "Expected application/x-protobuf.");
            var body = await OtlpBodyReader.ReadAsync(context.Request, parser, token);
            if (body.Status != OtlpBodyStatus.Success || body.Message is null)
                return body.Status switch
                {
                    OtlpBodyStatus.TooLarge => OtlpResponse.Failure(StatusCodes.Status413PayloadTooLarge, "Telemetry request is too large."),
                    OtlpBodyStatus.UnsupportedEncoding => OtlpResponse.Failure(StatusCodes.Status415UnsupportedMediaType, "Unsupported content encoding."),
                    _ => OtlpResponse.Failure(StatusCodes.Status400BadRequest, "Invalid telemetry request.")
                };
            // Old Web nodes authorize without namespace. Do not introduce namespaced
            // identities until all nodes have crossed a coordinated upgrade boundary.
            if (HasNamespace(body.Message))
                return OtlpResponse.Failure(StatusCodes.Status400BadRequest, "Service namespaces are not enabled during this deployment transition.");
            try
            {
                var result = await export(source, body.Message, token);
                return OtlpResponse.FromResult(context, result, response);
            }
            catch (DbException)
            {
                return OtlpResponse.Failure(StatusCodes.Status503ServiceUnavailable, "Telemetry storage is unavailable; retry later.");
            }
            catch (TimeoutException)
            {
                return OtlpResponse.Failure(StatusCodes.Status503ServiceUnavailable, "Telemetry storage timed out; retry later.");
            }
    }

    private static bool HasNamespace<T>(T message) => message switch
    {
        ExportLogsServiceRequest logs => logs.ResourceLogs.Any(x => NonEmptyNamespace(x.Resource)),
        ExportMetricsServiceRequest metrics => metrics.ResourceMetrics.Any(x => NonEmptyNamespace(x.Resource)),
        ExportTraceServiceRequest traces => traces.ResourceSpans.Any(x => NonEmptyNamespace(x.Resource)),
        _ => true
    };

    private static bool NonEmptyNamespace(Resource? resource) => resource?.Attributes.Any(x =>
        x.Key == "service.namespace" && (x.Value?.ValueCase != AnyValue.ValueOneofCase.StringValue || !string.IsNullOrEmpty(x.Value.StringValue))) == true;
}
