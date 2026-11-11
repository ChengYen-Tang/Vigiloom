using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;
using Tuf;

namespace Vigiloom.Web.Api;

internal static class AgentPackageEndpoints
{
    public static void MapAgentPackageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/agent-packages", async (int? page, HttpContext context, AgentPackageQuery query, CancellationToken token) =>
        {
            if (page is < 1 or > 20000)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["page"] = ["Page must be between 1 and 20000."] });
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await query.GetAsync(page ?? 1, token));
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<AgentPackagePage>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithSummary("List verified Agent package versions and catalogue availability")
            .WithDescription("Listed does not by itself authorize download: revocation, expiry, platform and assignment must also be checked.");
        app.MapPost("/api/v1/agent-packages/{id:guid}/cache", async (Guid id, PackageLibrary library, HttpContext context, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async _ =>
            {
                try { return await library.CacheAsync(id, token) ? Results.NoContent() : Results.Conflict(); }
                catch (InvalidOperationException) { return Results.Conflict(); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { return Results.Problem(statusCode: 503, title: "The package source is temporarily unavailable."); }
                catch (Exception error) when (error is HttpRequestException or TufException or IOException or System.Security.Cryptography.CryptographicException or TimeoutException)
                { return Results.Problem(statusCode: 503, title: "The package could not be verified or cached."); }
            }, allowApiCredential: false))
            .RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client")
            .Produces(204).ProducesProblem(401).ProducesProblem(403).Produces(409).ProducesProblem(429).ProducesProblem(503)
            .WithSummary("Verify and cache an eligible Agent package on this node")
            .WithDescription("Caching does not grant an Agent access or assign a version.");
    }
}
