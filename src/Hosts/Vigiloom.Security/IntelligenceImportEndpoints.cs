using Microsoft.AspNetCore.Mvc;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

namespace Vigiloom.Security;

internal static class IntelligenceImportEndpoints
{
    public static void MapIntelligenceImportEndpoints(this WebApplication app)
    {
        app.MapPost("/internal/v1/intelligence/sources/{sourceId:guid}/offline-import", async (Guid sourceId, Guid actorId, HttpContext context, IServiceProvider services, CancellationToken token) =>
        {
            var importer = services.GetService<KevOfflineImport>();
            if (importer is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            if (sourceId == Guid.Empty || actorId == Guid.Empty || context.Request.ContentLength is <= 0 or > 67108864) return Results.BadRequest();
            return Results.Ok(await importer.ImportAsync(sourceId, context.Request.Body, actorId, token));
        }).WithMetadata(new RequestSizeLimitAttribute(67108864));
    }
}
