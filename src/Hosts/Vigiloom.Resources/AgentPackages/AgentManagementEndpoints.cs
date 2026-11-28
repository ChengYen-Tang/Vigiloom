using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Workspace;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Modules.Resources.Application.AgentIdentity;
using Vigiloom.Modules.Resources.Application.AgentPackages;
using Vigiloom.UpdateSecurity.AgentPackages;

namespace Vigiloom.Resources.AgentPackages;

internal static class AgentManagementEndpoints
{
    internal static void MapAgentManagement(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/agent-management").RequireAuthorization(WorkspaceAccess.Administrator).RequireRateLimiting("api-client");
        admin.MapGet("/devices/{id:guid}", async (Guid id, HttpContext context, AgentRegistration agents, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await agents.ListAsync(id, token) is { } registrations ? Results.Ok(registrations) : Results.NotFound();
        });
        admin.MapPost("/devices/{id:guid}/enrollments", async (Guid id, HttpContext context, AgentRegistration agents, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor =>
            {
                var result = await agents.IssueAsync(id, actor, token);
                context.Response.Headers.CacheControl = "no-store";
                return result.Status switch
                {
                    AgentRegistrationStatus.Success => Results.Ok(new EnrollmentSecretResponse(result.Secret!, result.ExpiresAt!.Value)),
                    AgentRegistrationStatus.NotFound => Results.NotFound(),
                    _ => Results.Conflict()
                };
            }, allowApiCredential: false));
        admin.MapPost("/agents/{id:guid}/revoke", async (Guid id, HttpContext context, AgentRegistration agents, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor =>
                await agents.RevokeAsync(id, actor, token) == AgentRegistrationStatus.Success ? Results.NoContent() : Results.NotFound(), allowApiCredential: false));
        admin.MapPost("/enrollments/{id:guid}/revoke", async (Guid id, HttpContext context, AgentRegistration agents, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async actor =>
                await agents.RevokeEnrollmentAsync(id, actor, token) == AgentRegistrationStatus.Success ? Results.NoContent() : Results.NotFound(), allowApiCredential: false));

        app.MapGet("/api/v1/agent-releases", async (int? page, GitHubAgentReleases releases, CancellationToken token) =>
            page is < 1 ? Results.BadRequest() : Results.Ok(await releases.GetAsync(page ?? 1, token)))
            .RequireAuthorization(WorkspaceAccess.Administrator).RequireRateLimiting("api-client");
        app.MapGet("/api/v1/agent-packages", async (int? page, HttpContext context, PackageCatalog catalog, CancellationToken token) =>
        {
            if (page is < 1 or > 20000)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["page"] = ["Page must be between 1 and 20000."] });
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await catalog.ListAsync(page ?? 1, token));
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client");
        app.MapPost("/api/v1/agent-packages/cache", async (PackageDownload request, HttpContext context, PackageCatalog catalog, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async _ =>
            {
                try { return await catalog.DownloadAsync(request.Version, request.RuntimeIdentifier, token) ? Results.NoContent() : Results.NotFound(); }
                catch (InvalidOperationException) { return Results.Conflict(); }
                catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException)
                { return Results.Problem(statusCode: 503, title: "The package could not be downloaded."); }
            }, allowApiCredential: false)).RequireAuthorization(WorkspaceAccess.Administrator).RequireRateLimiting("api-client");
        app.MapDelete("/api/v1/agent-packages/{id:guid}/cache", async (Guid id, HttpContext context, PackageCatalog catalog, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async _ =>
            {
                try
                {
                    return await catalog.RemoveAsync(id, token) switch
                    {
                        AgentPackageRemovalResult.Removed => Results.NoContent(),
                        AgentPackageRemovalResult.InUse => Results.Conflict(),
                        _ => Results.NotFound()
                    };
                }
                catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException or InvalidOperationException)
                { return Results.Problem(statusCode: 503, title: "Agent package usage could not be checked."); }
            }, allowApiCredential: false)).RequireAuthorization(WorkspaceAccess.Administrator).RequireRateLimiting("api-client");
        app.MapGet("/api/v1/agents/{id:guid}/version-assignment", async (Guid id, HttpContext context, AgentAssignments assignments, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return await assignments.GetAsync(id, token) is { } assignment ? Results.Ok(assignment) : Results.NotFound();
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<AgentVersionAssignmentView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
            .WithSummary("Read a desired Agent package assignment");
        app.MapPut("/api/v1/agents/{id:guid}/version-assignment", async (Guid id, AgentVersionAssignmentUpdate input,
            HttpContext context, AgentAssignments assignments, CancellationToken token) =>
        {
            if (input.Revision is null || input.Revision.Length > 32 || input.PackageId == Guid.Empty)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["assignment"] = ["Invalid package or revision."] });
            return await ApiMutation.ExecuteAsync(context, async actor =>
                await assignments.SetAsync(id, new AgentVersionAssignmentCommand(input.PackageId, input.Revision, actor), token) switch
                {
                    VersionAssignmentResult.Saved => Results.NoContent(),
                    VersionAssignmentResult.NotFound => Results.NotFound(),
                    _ => Results.Problem(statusCode: 409, title: "Assignment changed or package is unavailable; reload and try again.")
                }, allowApiCredential: false);
        }).RequireAuthorization(WorkspaceAccess.Administrator).RequireRateLimiting("api-client")
            .Produces(204).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409)
            .WithSummary("Assign or cancel a desired Agent package");
    }

    private sealed record PackageDownload(string Version, string RuntimeIdentifier);
}
