using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class ServiceInstanceEndpoints
{
    public static void MapServiceInstanceEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/service-instances").RequireAuthorization(AccessPolicies.Administrator)
            .RequireRateLimiting("api-client");
        admin.MapGet("/services", async (ServiceInstanceAdministration management, HttpContext context, CancellationToken token, int page = 1) =>
        {
            if (page is < 1 or > 1000) return Results.BadRequest();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await management.ListServicesAsync(page, token));
        }).Produces<IReadOnlyList<ManagedServiceSummary>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);
        admin.MapGet("/services/{serviceId:guid}", async (Guid serviceId, ServiceInstanceAdministration management, HttpContext context, CancellationToken token, int page = 1) =>
        {
            if (serviceId.Version != 7 || page is < 1 or > 1000) return Results.BadRequest();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await management.ListAsync(serviceId, page, token));
        }).Produces<IReadOnlyList<ServiceInstanceSummary>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);
        admin.MapGet("/policy", async (InstancePolicyAdministration policy, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await policy.GetAsync(token));
        }).Produces<InstancePolicySnapshot>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);
        admin.MapPut("/policy", async (InstancePolicyInput input, HttpContext context, InstancePolicyAdministration policy, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor =>
            {
                if (input.PerService <= 0 || input.Total <= 0) return Results.BadRequest();
                var saved = await policy.UpdateAsync(input, actor, token);
                return saved is null ? Results.Conflict() : Results.Ok(saved);
            }, allowApiCredential: false))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(2048))
            .Produces<InstancePolicySnapshot>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429);
        admin.MapPost("/{instanceId:guid}/{action}", async (Guid instanceId, string action, ServiceInstanceUpdate request,
            HttpContext context, ServiceInstanceAdministration management, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor =>
            {
                if (instanceId.Version != 7 || action is not ("retire" or "restore")) return Results.BadRequest();
                return await management.SetRetiredAsync(instanceId, request.Version, action == "retire", actor, token) switch
                {
                    InstanceChangeStatus.Updated => Results.NoContent(),
                    InstanceChangeStatus.NotFound => Results.NotFound(),
                    InstanceChangeStatus.Conflict => Results.Conflict(),
                    _ => Results.Problem(statusCode: 503, title: "Instance management is busy; retry later.")
                };
            }, allowApiCredential: false))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(2048))
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(413).ProducesProblem(429).ProducesProblem(503);
    }
}
