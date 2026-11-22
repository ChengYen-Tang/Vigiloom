using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Tuf;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class AgentPackageUiEndpoints
{
    public static void MapAgentPackageUiEndpoints(this WebApplication app)
    {
        app.MapPost("/settings/agent-packages/{id:guid}/download", async (Guid id, HttpContext context, PackageLibrary library,
            ILogger<PackageLibrary> logger, CancellationToken token) => await ApiMutation.ExecuteAsync(context, async _ =>
        {
            try
            {
                var page = int.TryParse(context.Request.Query["page"], out var number) && number > 0 ? number : 1;
                var cached = await library.CacheAsync(id, token);
                return Results.Redirect($"/settings/agent-packages?releases={page}&download={(cached ? "saved" : "unavailable")}");
            }
            catch (Exception error) when (error is IOException or HttpRequestException or TufException or InvalidOperationException)
            {
                logger.LogWarning("Could not download Agent package: {ErrorType}", error.GetType().Name);
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
        app.MapPost("/settings/agent-packages/trust", async ([FromForm] IFormFile root, [FromForm] string sha256,
            HttpContext context, UpdateTrustStore trust, ILogger<UpdateTrustStore> logger, CancellationToken token) =>
        {
            if (!Guid.TryParse(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var actor) || actor == Guid.Empty)
                return Results.Forbid();
            if (root.Length is <= 0 or > 1048576) return Results.Redirect("/settings/agent-packages?trust=invalid");
            try
            {
                await using var stream = new MemoryStream((int)root.Length);
                await root.CopyToAsync(stream, token);
                await trust.ProvisionForAdministratorAsync(stream.ToArray(), sha256.Trim().ToLowerInvariant(), actor, token);
                return Results.Redirect("/settings/agent-packages?trust=saved");
            }
            catch (InvalidOperationException) { return Results.Redirect("/settings/agent-packages?trust=exists"); }
            catch (Exception error) when (error is ArgumentException or InvalidDataException or TufException or System.Text.Json.JsonException)
            { return Results.Redirect("/settings/agent-packages?trust=invalid"); }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            {
                logger.LogWarning("Could not initialize package trust: {ErrorType}", error.GetType().Name);
                return Results.Redirect("/settings/agent-packages?trust=error");
            }
        }).RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client")
          .WithMetadata(new RequestSizeLimitAttribute(1200000));
    }
}
