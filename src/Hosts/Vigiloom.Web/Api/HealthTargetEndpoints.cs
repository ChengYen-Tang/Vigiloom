using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Health;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class HealthTargetEndpoints
{
    internal static void MapHealthTargetEndpoints(this WebApplication app)
    {
        var networks = app.MapGroup("/api/v1/service-health/networks")
            .RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
        networks.MapGet("/", async (HealthNetworkPolicyAdministration policy, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await policy.GetAsync(token));
        }).WithSummary("Get allowed health check networks").Produces<HealthNetworkPolicySnapshot>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);
        networks.MapGet("/history", async (int? page, HealthNetworkPolicyAdministration policy, HttpContext context, CancellationToken token) =>
        {
            if (page is < 1 or > 1000) return Results.BadRequest();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await policy.ListRevisionsAsync(page ?? 1, token));
        }).WithSummary("List prior health check network policies")
            .Produces<IReadOnlyList<HealthNetworkRevisionSnapshot>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);
        networks.MapPut("/", async (HealthNetworkPolicyInput input, HttpContext context, HealthNetworkPolicyAdministration policy, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor =>
            {
                try
                {
                    var saved = await policy.UpdateAsync(input, actor, token);
                    return saved is null ? Results.Conflict() : Results.Ok(saved);
                }
                catch (ArgumentException) { return Results.BadRequest(); }
            }, allowApiCredential: false))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(8192))
            .WithSummary("Update allowed health check networks")
            .Produces<HealthNetworkPolicySnapshot>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
        var group = app.MapGroup("/api/v1/service-health/targets")
            .RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
        group.MapGet("/", async (Guid serviceId, int? page, HealthTargetAdministration service, HttpContext context, CancellationToken token) =>
        {
            if (serviceId.Version != 7 || page is < 1 or > 1000) return Results.BadRequest();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await service.ListAsync(serviceId, page ?? 1, token));
        }).WithSummary("List configured health checks for a service")
            .Produces<IReadOnlyList<HealthTargetSnapshot>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);
        group.MapGet("/observations", async (Guid serviceId, int? page, HealthObservationReader reader, HttpContext context, CancellationToken token) =>
        {
            if (serviceId.Version != 7 || page is < 1 or > 1000) return Results.BadRequest();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await reader.LatestAsync(serviceId, page ?? 1, token));
        }).WithSummary("Get latest recorded health check observations for a service")
            .Produces<IReadOnlyList<HealthCheckObservation>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);
        group.MapGet("/{id:guid}", async (Guid id, HealthTargetAdministration service, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var target = await service.GetAsync(id, token);
            return target is null ? Results.NotFound() : Results.Ok(target);
        }).WithSummary("Get a configured health check")
            .Produces<HealthTargetSnapshot>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
        group.MapPost("/", async (CreateHealthTargetInput input, HttpContext context, HealthTargetAdministration service, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor =>
            {
                var result = await service.CreateAsync(input, actor, token);
                return result.Status == HealthTargetStatus.Saved
                    ? Results.Created($"/api/v1/service-health/targets/{result.Target!.Id:D}", result.Target)
                    : Error(result.Status);
            }, allowApiCredential: false))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(4096))
            .WithSummary("Create a health check for an approved service")
            .Produces<HealthTargetSnapshot>(201).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
        group.MapPut("/{id:guid}", async (Guid id, HealthTargetInput input, HttpContext context, HealthTargetAdministration service, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor =>
            {
                var result = await service.UpdateAsync(id, input, actor, token);
                return result.Status == HealthTargetStatus.Saved ? Results.Ok(result.Target) : Error(result.Status);
            }, allowApiCredential: false))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(4096))
            .WithSummary("Update a health check using its version")
            .Produces<HealthTargetSnapshot>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
    }

    private static IResult Error(HealthTargetStatus status) => status switch
    {
        HealthTargetStatus.NotFound => Results.NotFound(),
        HealthTargetStatus.Conflict => Results.Conflict(),
        HealthTargetStatus.Unavailable => Results.Conflict(new { message = "The service source or instance is unavailable." }),
        HealthTargetStatus.LimitReached => Results.Conflict(new { message = "The service has reached its health check limit." }),
        _ => Results.BadRequest()
    };
}
