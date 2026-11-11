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
