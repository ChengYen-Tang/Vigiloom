using Microsoft.AspNetCore.Mvc;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class AgentPackageUiEndpoints
{
    public static void MapAgentPackageUiEndpoints(this WebApplication app)
    {
        app.MapGet("/settings/agent-tools/grant-sysmon-read", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var script = typeof(AgentPackageUiEndpoints).Assembly.GetManifestResourceStream("Vigiloom.Agent.GrantSysmonRead.ps1")
                ?? throw new InvalidOperationException("Sysmon permission script is missing.");
            return Results.File(script, "application/octet-stream", "grant-sysmon-read.ps1");
        }).RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
        app.MapPost("/settings/agent-packages/download", async ([FromForm] string version, [FromForm] string runtimeIdentifier,
            HttpContext context, PackageLibrary library, ILogger<PackageLibrary> logger, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async _ =>
            {
                try
                {
                    var cached = await library.CacheAsync(version, runtimeIdentifier, token);
                    return Results.Redirect($"/settings/agent-packages?releases=1&download={(cached ? "saved" : "unavailable")}");
                }
                catch (InvalidOperationException error)
                {
                    logger.LogWarning(error, "Agent package conflicts with an existing version or assignment.");
                    return Results.Redirect("/settings/agent-packages?releases=1&download=conflict");
                }
                catch (Exception error) when (error is IOException or HttpRequestException)
                {
                    logger.LogWarning(error, "Could not download Agent package.");
                    return Results.Redirect("/settings/agent-packages?releases=1&download=error");
                }
            }, allowApiCredential: false)).RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");

        app.MapPost("/settings/agent-packages/{id:guid}/remove", async (Guid id, HttpContext context, PackageLibrary library,
            ILogger<PackageLibrary> logger, CancellationToken token) => await ApiMutation.ExecuteAsync(context, async _ =>
        {
            try
            {
                var result = await library.RemoveAsync(id, token);
                return Results.Redirect(result switch
                {
                    PackageRemovalResult.Removed => "/settings/agent-packages?remove=removed",
                    PackageRemovalResult.InUse => "/settings/agent-packages?remove=in-use",
                    _ => "/settings/agent-packages?remove=missing"
                });
            }
            catch (IOException error)
            {
                logger.LogWarning(error, "Could not remove cached Agent package.");
                return Results.Redirect("/settings/agent-packages?remove=error");
            }
        }, allowApiCredential: false)).RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
    }
}
