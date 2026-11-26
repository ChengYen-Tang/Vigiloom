using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines;

namespace Vigiloom.Security;

internal static class CustomBaselineEndpoints
{
    public static void MapCustomBaselineEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/internal/v1/intelligence/custom-baselines");
        group.MapPost("/stage", (StageCommand command, CustomBaselineStage stage, CancellationToken token) =>
            stage.SaveAsync(command.SourceId, command.ExpectedVersion, command.Check, command.ActorId, token));
        group.MapGet("/{sourceId:guid}/revisions/{revisionId:guid}", async (Guid sourceId, Guid revisionId, CustomBaselineStage stage, CancellationToken token) =>
            await stage.ReadAsync(sourceId, revisionId, token) is { } check ? Results.Ok(check) : Results.NotFound());
        group.MapGet("/{sourceId:guid}/revisions/{revisionId:guid}/draft", async (Guid sourceId, Guid revisionId, CustomBaselineStage stage, CancellationToken token) =>
            await stage.ReadDraftAsync(sourceId, revisionId, token) is { } draft ? Results.Ok(draft) : Results.NotFound());
        group.MapPost("/{sourceId:guid}/versions", (Guid sourceId, Guid[] revisionIds, CustomBaselineStage stage, CancellationToken token) =>
            stage.ReadVersionsAsync(sourceId, revisionIds, token));
        group.MapGet("/{sourceId:guid}/page", (Guid sourceId, CustomBaselineStage stage, CancellationToken token) => stage.SourcePageAsync(sourceId, token));
        group.MapGet("/{sourceId:guid}/active", (Guid sourceId, IBaselineSource sources, CancellationToken token) =>
            sources.GetActiveAsync($"{BaselineSources.LocalPrefix}{sourceId:D}", token));
    }

    private sealed record StageCommand(Guid? SourceId, string? ExpectedVersion, BaselineCheck Check, Guid ActorId);
}
