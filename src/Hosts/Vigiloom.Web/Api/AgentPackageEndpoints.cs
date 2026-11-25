using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class AgentPackageEndpoints
{
    public static void MapAgentPackageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/agent-packages", async (int? page, HttpContext context, IAgentPackageQuery query, CancellationToken token) =>
        {
            if (page is < 1 or > 20000)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["page"] = ["Page must be between 1 and 20000."] });
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await query.GetAsync(page ?? 1, token));
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<AgentPackagePage>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithSummary("List downloaded Agent package versions");

        app.MapPost("/api/v1/agent-packages/cache", async (AgentPackageDownloadRequest request, IAgentPackageLibrary library, HttpContext context, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async _ =>
            {
                try { return await library.CacheAsync(request.Version, request.RuntimeIdentifier, token) ? Results.NoContent() : Results.NotFound(); }
                catch (InvalidOperationException) { return Results.Conflict(); }
                catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException)
                { return Results.Problem(statusCode: 503, title: "The package could not be downloaded."); }
            }, allowApiCredential: false))
            .RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client")
            .Produces(204).ProducesProblem(401).ProducesProblem(403).Produces(404).Produces(409).ProducesProblem(429).ProducesProblem(503)
            .WithSummary("Download an Agent release to the central platform");

        app.MapDelete("/api/v1/agent-packages/{id:guid}/cache", async (Guid id, IAgentPackageLibrary library, HttpContext context, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async _ => (await library.RemoveAsync(id, token)) switch
            {
                PackageRemovalResult.Removed => Results.NoContent(),
                PackageRemovalResult.InUse => Results.Conflict(),
                _ => Results.NotFound()
            }, allowApiCredential: false))
            .RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client")
            .Produces(204).ProducesProblem(401).ProducesProblem(403).Produces(404).Produces(409)
            .WithSummary("Remove an unused Agent package");
    }
}

internal sealed record AgentPackageDownloadRequest(string Version, string RuntimeIdentifier);
