using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;

namespace Vigiloom.Web.Api;

internal static class TelemetryHistoryEndpoints
{
    internal static void MapTelemetryHistoryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/service-reception/collector-observations/{serviceId:guid}", async (Guid serviceId,
            CollectorObservations observations, CancellationToken token, int page = 1) =>
        {
            if (page is < 1 or > 2_000) return Results.BadRequest();
            var result = await observations.QueryAsync(serviceId, page, token);
            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireAuthorization(Vigiloom.Web.Authentication.AccessPolicies.Administrator).RequireRateLimiting("api-client")
            .Produces<CollectorObservationPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429)
            .WithSummary("Inspect recent uptime reports for a selected monitoring service");
        app.MapGet("/api/v1/service-reception/collector-template", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var stream = typeof(TelemetryHistoryEndpoints).Assembly.GetManifestResourceStream("Vigiloom.Collector.IsolatedSources.yaml")
                ?? throw new InvalidOperationException("Collector template is missing from the application package.");
            return Results.File(stream, "application/yaml", "vigiloom-collector-example.yaml");
        }).RequireAuthorization(Vigiloom.Web.Authentication.AccessPolicies.Administrator).RequireRateLimiting("api-client")
            .Produces<byte[]>(StatusCodes.Status200OK, contentType: "application/yaml").ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithSummary("Download the isolated-source Collector configuration example");
        app.MapGet("/api/v1/service-reception/application-template", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var stream = typeof(TelemetryHistoryEndpoints).Assembly.GetManifestResourceStream("Vigiloom.Collector.Application.env")
                ?? throw new InvalidOperationException("Application template is missing from the application package.");
            return Results.File(stream, "text/plain; charset=utf-8", "vigiloom-application.env.example");
        }).RequireAuthorization(Vigiloom.Web.Authentication.AccessPolicies.Administrator).RequireRateLimiting("api-client")
            .Produces<byte[]>(StatusCodes.Status200OK, contentType: "text/plain").ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithSummary("Download the application SDK configuration example");
        app.MapGet("/api/v1/service-reception", async (ReceptionOverview overview, CancellationToken token, int page = 1) =>
        {
            if (page is < 1 or > 200) return Results.BadRequest();
            return Results.Ok(await overview.ListAsync(page, token));
        }).RequireAuthorization(Vigiloom.Web.Authentication.AccessPolicies.Administrator).RequireRateLimiting("api-client")
            .Produces<IReadOnlyList<SourceReceptionSummary>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithSummary("Inspect the latest persisted telemetry for each source");
        var group = app.MapGroup("/api/v1/service-telemetry/{serviceId:guid}")
            .RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client");
        group.MapGet("/logs", async (Guid serviceId, DateTimeOffset from, DateTimeOffset to, TelemetryHistory history,
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
    }

    private static bool TryTraceId(string? value, out byte[]? trace)
    {
        trace = null;
        if (value is null) return true;
        if (value.Length != 32) return false;
        try { trace = Convert.FromHexString(value); return trace.AsSpan().IndexOfAnyExcept((byte)0) >= 0; }
        catch (FormatException) { return false; }
    }
}
