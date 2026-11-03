using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Contracts.Services;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class ServiceDiscoveryEndpoints
{
    public static void MapServiceDiscoveryEndpoints(this WebApplication app)
    {
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
