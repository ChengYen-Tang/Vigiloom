using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Rollups;

namespace Vigiloom.Observability;

internal static class TelemetryHistoryEndpoints
{
    internal static void MapTelemetryHistoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/service-telemetry/{serviceId:guid}")
            .RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client");
        group.MapGet("/logs", async (Guid serviceId, DateTimeOffset from, DateTimeOffset to, IApplicationLogHistory history,
            CancellationToken token, int page = 1, string? traceId = null) =>
        {
            if (!TryTraceId(traceId, out var trace)) return Results.BadRequest();
            try { return Results.Ok(await history.LogsAsync(serviceId, from, to, page, trace, token)); }
            catch (ArgumentException) { return Results.BadRequest(); }
        }).Produces<TelemetryPage<TelemetryLog, TelemetryContext>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithSummary("Query a service's retained OTLP logs");
        group.MapGet("/traces", async (Guid serviceId, DateTimeOffset from, DateTimeOffset to, TelemetryHistory history,
            CancellationToken token, int page = 1, string? traceId = null) =>
        {
            if (!TryTraceId(traceId, out var trace)) return Results.BadRequest();
            try { return Results.Ok(await history.TracesAsync(serviceId, from, to, page, trace, token)); }
            catch (ArgumentException) { return Results.BadRequest(); }
        }).Produces<TelemetryPage<TelemetrySpan, TelemetryContext>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithSummary("Query a service's retained OTLP spans");
        group.MapGet("/metrics", async (Guid serviceId, DateTimeOffset from, DateTimeOffset to, TelemetryHistory history,
            CancellationToken token, int page = 1) =>
        {
            try { return Results.Ok(await history.MetricsAsync(serviceId, from, to, page, token)); }
            catch (ArgumentException) { return Results.BadRequest(); }
        }).Produces<TelemetryPage<TelemetryMetricPoint, MetricTelemetryContext>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithSummary("Query a service's retained OTLP metric points");
        group.MapGet("/metrics/windows", async (Guid serviceId, DateTimeOffset from, DateTimeOffset to, TelemetryHistory history,
            CancellationToken token, DateTimeOffset? before = null) =>
        {
            try { return Results.Ok(await history.MetricWindowsAsync(serviceId, from, to, before, token)); }
            catch (ArgumentException) { return Results.BadRequest(); }
        }).Produces<MetricRollupWindowPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithSummary("List a service's five-minute metric window status");
        group.MapGet("/metrics/windows/series", async (Guid serviceId, DateTimeOffset start, string version, TelemetryHistory history,
            CancellationToken token, int page = 1, Guid? instanceId = null) =>
        {
            try
            {
                var result = await history.MetricSeriesAsync(serviceId, start, version, page, instanceId, token);
                return result.Status switch
                {
                    MetricRollupReadStatus.NotFound => Results.NotFound(),
                    MetricRollupReadStatus.Changed or MetricRollupReadStatus.UnsupportedVersion => RollupConflict(result.Status),
                    _ => Results.Ok(result.Page)
                };
            }
            catch (ArgumentException) { return Results.BadRequest(); }
        }).Produces<MetricRollupSeriesPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429)
            .WithSummary("List metric series for an unchanged five-minute window");
        group.MapGet("/metrics/windows/payload", async (HttpContext context, Guid serviceId, DateTimeOffset start, string version,
            TelemetryHistory history, MetricRollupPayloadGate admission, CancellationToken token, int page = 1, int position = 0, Guid? instanceId = null) =>
            await SendMetricPayloadAsync(context, serviceId, start, version, history, admission, token, page, position, instanceId))
            .Produces<byte[]>(StatusCodes.Status200OK, contentType: "application/x-protobuf").ProducesProblem(400).ProducesProblem(401)
            .ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429)
            .Produces(StatusCodes.Status503ServiceUnavailable).Produces(StatusCodes.Status504GatewayTimeout)
            .WithSummary("Download an unchanged metric series result as OTLP ResourceMetrics protobuf");
    }

    internal static async Task<IResult> SendMetricPayloadAsync(HttpContext context, Guid serviceId, DateTimeOffset start, string version,
        TelemetryHistory history, MetricRollupPayloadGate admission, CancellationToken token, int page, int position, Guid? instanceId)
    {
        if (!await admission.Gate.WaitAsync(0, token))
        {
            context.Response.Headers.RetryAfter = "1";
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, admission.DeadlineSignal);
        deadline.CancelAfter(admission.Deadline);
        try
        {
            var result = await history.MetricPayloadAsync(serviceId, start, version, page, position, instanceId, deadline.Token);
            if (result.Status == MetricRollupReadStatus.NotFound) return Results.NotFound();
            if (result.Status == MetricRollupReadStatus.Changed || result.Status == MetricRollupReadStatus.UnsupportedVersion) return RollupConflict(result.Status);
            context.Response.ContentType = "application/x-protobuf";
            context.Response.ContentLength = result.Payload!.LongLength;
            await context.Response.Body.WriteAsync(result.Payload, deadline.Token);
            return Results.Empty;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.ContentLength = null;
                context.Response.ContentType = null;
                return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
            }
            context.Abort();
            return Results.Empty;
        }
        catch (ArgumentException) { return Results.BadRequest(); }
        finally { admission.Gate.Release(); }
    }

    private static IResult RollupConflict(MetricRollupReadStatus status) => Results.Problem(
        statusCode: StatusCodes.Status409Conflict,
        title: status == MetricRollupReadStatus.Changed ? "The metric window changed; reload it before continuing." : "The metric result version is not supported.",
        extensions: new Dictionary<string, object?> { ["code"] = status == MetricRollupReadStatus.Changed ? "window_changed" : "unsupported_version" });

    private static bool TryTraceId(string? value, out byte[]? trace)
    {
        trace = null;
        if (value is null) return true;
        if (value.Length != 32) return false;
        try { trace = Convert.FromHexString(value); return trace.AsSpan().IndexOfAnyExcept((byte)0) >= 0; }
        catch (FormatException) { return false; }
    }
}
