using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Contracts.Intelligence;

namespace Vigiloom.Security;

internal static class IntelligenceStatusEndpoints
{
    public static void MapIntelligenceStatusEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/internal/v1/intelligence/sources");
        group.MapPost("/{sourceId:guid}/activation", async (Guid sourceId, ActivationCommand command, IServiceProvider services, CancellationToken token) =>
        {
            var publication = services.GetService<IntelligencePublication>();
            if (publication is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            try { return Results.Ok(await publication.ActivateAsync(sourceId, command.Input.RevisionId, command.Input.ExpectedActiveRevisionId,
                command.Input.ExpectedVersion, command.Input.ConfirmLargeReduction, command.Input.ConfirmOfflineSource, command.ActorId, token)); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        group.MapPost("/{sourceId:guid}/revisions/{revisionId:guid}/revoke", async (Guid sourceId, Guid revisionId, RevocationCommand command, IServiceProvider services, CancellationToken token) =>
        {
            var revocation = services.GetService<IntelligenceRevocation>();
            if (revocation is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            try { return Results.Ok(await revocation.RevokeAsync(sourceId, revisionId, command.ActorId, token)); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        group.MapGet("/", async (int page, IntelligenceStatus status, CancellationToken token) =>
            page is < 1 or > 1000 ? Results.BadRequest() : Results.Ok(await status.GetSourcesAsync(page, token)));
        group.MapGet("/{sourceId:guid}/revisions", async (Guid sourceId, int page, IntelligenceStatus status, CancellationToken token) =>
            page is < 1 or > 1000 ? Results.BadRequest() : await status.GetRevisionsAsync(sourceId, page, token) is { } result ? Results.Ok(result) : Results.NotFound());
        group.MapGet("/{sourceId:guid}/advisory-progress", async (Guid sourceId, IntelligenceStatus status, CancellationToken token) =>
            await status.GetAdvisoryProgressAsync(sourceId, token) is { } result ? Results.Ok(result) : Results.NotFound());
        group.MapGet("/{sourceId:guid}/revisions/{revisionId:guid}/sigma-rules", async (Guid sourceId, Guid revisionId, int page, IntelligenceStatus status, CancellationToken token) =>
            page is < 1 or > 1000 ? Results.BadRequest() : await status.GetSigmaRulesAsync(sourceId, revisionId, page, token) is { } result ? Results.Ok(result) : Results.NotFound());
        group.MapGet("/{sourceId:guid}/revisions/{revisionId:guid}/signature-files", async (Guid sourceId, Guid revisionId, int page, IntelligenceStatus status, CancellationToken token) =>
            page is < 1 or > 1000 ? Results.BadRequest() : await status.GetSignatureFilesAsync(sourceId, revisionId, page, token) is { } result ? Results.Ok(result) : Results.NotFound());
        group.MapGet("/{sourceId:guid}/revisions/{revisionId:guid}/ioc-issues", async (Guid sourceId, Guid revisionId, string path, int page, IntelligenceStatus status, CancellationToken token) =>
            page is < 1 or > 2000 || path.Length is < 1 or > 512 ? Results.BadRequest() : await status.GetIocIssuesAsync(sourceId, revisionId, path, page, token) is { } result ? Results.Ok(result) : Results.NotFound());
        group.MapGet("/{sourceId:guid}/revisions/{revisionId:guid}/signature-evidence", async (Guid sourceId, Guid revisionId, string path, int? line, int page, IntelligenceStatus status, CancellationToken token) =>
            page is < 1 or > 2000 || path.Length is < 1 or > 512 || line is < 1 or > 1048576 ? Results.BadRequest() : await status.GetSignatureEvidenceAsync(sourceId, revisionId, path, token, line, page) is { } result ? Results.Ok(result) : Results.NotFound());
    }

    private sealed record ActivationCommand(IntelligenceActivationRequest Input, Guid ActorId);
    private sealed record RevocationCommand(Guid ActorId);
}
