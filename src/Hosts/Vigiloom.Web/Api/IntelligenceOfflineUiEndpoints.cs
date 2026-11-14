using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class IntelligenceOfflineUiEndpoints
{
    internal static void MapIntelligenceOfflineUiEndpoints(this WebApplication app)
    {
        app.MapPost("/settings/intelligence/{sourceId:guid}/offline-import", async (Guid sourceId, [FromForm] IFormFile file,
            HttpContext context, KevOfflineImport importer, ILogger<KevOfflineImport> logger, CancellationToken token) =>
        {
            if (!Guid.TryParse(context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var actor) || actor == Guid.Empty)
                return Results.Forbid();
            if (file.Length is <= 0 or > 67108864) return Redirect(sourceId, "invalid");
            try
            {
                await using var input = file.OpenReadStream();
                var result = await importer.ImportAsync(sourceId, input, actor, token);
                return Redirect(sourceId, result switch
                {
                    KevOfflineImportResult.Imported => "saved", KevOfflineImportResult.Invalid => "invalid",
                    KevOfflineImportResult.Unavailable => "unavailable", _ => "retry"
                });
            }
            catch (Exception error) when (error is not OperationCanceledException || !token.IsCancellationRequested)
            {
                logger.LogWarning("Offline intelligence import failed: {ErrorType}", error.GetType().Name);
                return Redirect(sourceId, "error");
            }
        }).RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client")
            .WithMetadata(new RequestSizeLimitAttribute(68157440));
    }

    private static IResult Redirect(Guid sourceId, string result) => Results.Redirect($"/settings/intelligence?source={sourceId:D}&import={result}");
}
