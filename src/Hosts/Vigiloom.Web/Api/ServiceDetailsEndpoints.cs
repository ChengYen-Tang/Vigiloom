using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using ServiceDetailsDto = Vigiloom.Contracts.Services.ServiceDetails;

namespace Vigiloom.Web.Api;

internal static class ServiceDetailsEndpoints
{
    internal static void MapServiceDetailsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/service-details/{serviceId:guid}")
            .RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client");
        group.MapGet("/", async (Guid serviceId, ServiceDetailsReader reader, HttpContext context, CancellationToken token) =>
        {
            if (serviceId.Version != 7) return Results.BadRequest();
            context.Response.Headers.CacheControl = "no-store";
            var service = await reader.ReadAsync(serviceId, token);
            return service is null ? Results.NotFound() : Results.Ok(service);
        }).Produces<ServiceDetailsDto>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Get an approved application service");
        group.MapGet("/instances", async (Guid serviceId, ServiceDetailsReader reader, HttpContext context, CancellationToken token, int page = 1) =>
        {
            if (serviceId.Version != 7 || page is < 1 or > 1000) return Results.BadRequest();
            context.Response.Headers.CacheControl = "no-store";
            var instances = await reader.InstancesAsync(serviceId, page, token);
            return instances is null ? Results.NotFound() : Results.Ok(instances);
        }).Produces<ServiceInstancePage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("List registered application service instances");
        group.MapGet("/logs", async (Guid serviceId, DateTimeOffset from, DateTimeOffset to, ServiceEvidenceReader reader,
            HttpContext context, CancellationToken token, int page = 1, string? traceId = null) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { return Results.Ok(await reader.LogsAsync(serviceId, from.ToUniversalTime(), to.ToUniversalTime(), page, traceId, token)); }
            catch (ArgumentException) { return Results.BadRequest(); }
        }).Produces<ServiceEvidencePage<ServiceLogEvidence>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Read retained service log evidence");
        group.MapGet("/traces", async (Guid serviceId, DateTimeOffset from, DateTimeOffset to, ServiceEvidenceReader reader,
            HttpContext context, CancellationToken token, int page = 1) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            try { return Results.Ok(await reader.TracesAsync(serviceId, from.ToUniversalTime(), to.ToUniversalTime(), page, token)); }
            catch (ArgumentException) { return Results.BadRequest(); }
        }).Produces<ServiceEvidencePage<ServiceTraceEvidence>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Read retained service trace evidence");
        group.MapGet("/http-metrics", async (Guid serviceId, ServiceHttpMetricsReader reader, HttpContext context, CancellationToken token) =>
        {
            if (serviceId.Version != 7) return Results.BadRequest();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await reader.ReadAsync(serviceId, token));
        }).Produces<ServiceHttpMetrics>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Summarize received HTTP duration histograms");
        group.MapGet("/health-observations", async (Guid serviceId, HealthObservationReader reader, HttpContext context, CancellationToken token, int page = 1) =>
        {
            if (serviceId.Version != 7 || page is < 1 or > 1000) return Results.BadRequest();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await reader.PageAsync(serviceId, page, token));
        }).Produces<HealthObservationPage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Read latest health check observations");
    }
}
