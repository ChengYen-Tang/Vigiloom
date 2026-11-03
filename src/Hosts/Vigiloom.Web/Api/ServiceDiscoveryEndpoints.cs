using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Contracts.Services;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class ServiceDiscoveryEndpoints
{
    public static void MapServiceDiscoveryEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/service-discovery/observations", ServiceObservationEndpoint.ReceiveAsync)
            .RequireAuthorization("ServiceSource").RequireRateLimiting("service-discovery")
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(ServiceObservationEndpoint.MaximumBytes))
            .Accepts<ServiceObservation>("application/json")
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(413).ProducesProblem(415).ProducesProblem(429).ProducesProblem(503);
        var sources = app.MapGroup("/api/v1/service-discovery/sources")
            .RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
        sources.MapGet("/", async (HttpContext context, SourceAdministration administration, CancellationToken token, int page = 1) =>
        {
            if (page is < 1 or > 200) return Results.BadRequest();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await administration.ListAsync(page, token));
        }).Produces<IReadOnlyList<ServiceSourceSummary>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);
        sources.MapPost("/", async (ServiceSourceRequest request, HttpContext context, SourceAdministration administration, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor =>
            {
                if (!context.Request.IsHttps || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 128) return Results.BadRequest();
                var issued = await administration.IssueAsync(request.Name, actor, token);
                context.Response.Headers.CacheControl = "no-store";
                return Results.Ok(new ServiceSourceCredential(issued.SourceId, issued.Secret));
            }, allowApiCredential: false))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(2048))
            .Produces<ServiceSourceCredential>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(413).ProducesProblem(429);
        sources.MapPost("/{sourceId:guid}/revoke", async (Guid sourceId, HttpContext context, SourceAdministration administration, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor =>
            {
                if (sourceId.Version != 7) return Results.BadRequest();
                return await administration.RevokeAsync(sourceId, actor, token) ? Results.NoContent() : Results.NotFound();
            }, allowApiCredential: false))
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
        var admin = app.MapGroup("/api/v1/service-discovery/candidates")
            .RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
        admin.MapGet("/", async (HttpContext context, CandidateDiscovery candidates, CancellationToken token, int page = 1) =>
        {
            if (page is < 1 or > 200) return Results.ValidationProblem(new Dictionary<string, string[]> { ["page"] = ["Use a page from 1 to 200."] });
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await candidates.ListAsync(page, token));
        }).Produces<IReadOnlyList<CandidateSummary>>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429);

        admin.MapPost("/{candidateId:guid}/approve", async (Guid candidateId, HttpContext context, ServiceApproval approval, CancellationToken token) =>
        {
            if (candidateId.Version != 7) return Results.ValidationProblem(new Dictionary<string, string[]> { ["candidateId"] = ["Use a version 7 candidate ID."] });
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                var result = await approval.ApproveAsync(candidateId, actor, token);
                return result.Status switch
                {
                    ApprovalStatus.Approved or ApprovalStatus.AlreadyApproved when result.ServiceId is { } id => Results.Ok(new ServiceApprovalResponse(id)),
                    ApprovalStatus.NotFound => Results.NotFound(),
                    ApprovalStatus.Expired or ApprovalStatus.SourceUnavailable => Results.Problem(statusCode: 409, title: "The candidate is no longer eligible for approval."),
                    _ => Results.Problem(statusCode: 503, title: "Discovery is busy; retry later.")
                };
            }, allowApiCredential: false);
        }).Produces<ServiceApprovalResponse>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);
    }
}
