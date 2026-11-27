using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Modules.AgentManagement.Application.HostMetrics;

namespace Vigiloom.HostMonitoring;

internal static class HostMetricRetentionEndpoints
{
    public static void MapHostMetricRetentionEndpoints(this WebApplication app)
    {
        app.MapGet("/internal/v1/host-metrics/retention", async (HostOwnedMetricRetentionSettings settings, CancellationToken token) => Results.Ok(await settings.GetAsync(token)));
        app.MapPost("/internal/v1/host-metrics/retention/preview", async (HostMetricRetentionPreviewCommand command, HostOwnedMetricRetentionSettings settings, CancellationToken token) =>
        {
            if (command.Input is null || command.Actor == Guid.Empty || !HostMetricRetentionLimits.IsValid(command.Input.RawDays, command.Input.RollupDays)) return Results.BadRequest();
            return await settings.PreviewAsync(command.Input, command.Actor, token) is { } preview ? Results.Ok(preview) : Results.Conflict();
        });
        app.MapPost("/internal/v1/host-metrics/retention", async (HostMetricRetentionCommand command, HostOwnedMetricRetentionSettings settings, CancellationToken token) =>
        {
            if (command.Input is null || command.Actor == Guid.Empty || !HostMetricRetentionLimits.IsValid(command.Input.RawDays, command.Input.RollupDays)) return Results.BadRequest();
            try { return await settings.UpdateAsync(command.Input, command.Actor, token) is { } updated ? Results.Ok(updated) : Results.Conflict(); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        var admin = app.MapGroup("/api/v1/settings/host-metrics/retention").RequireAuthorization("Administrator");
        admin.MapGet("", async (HostOwnedMetricRetentionSettings settings, CancellationToken token) => Results.Ok(await settings.GetAsync(token)))
            .Produces<HostMetricRetentionSnapshot>().ProducesProblem(401).ProducesProblem(403);
        admin.MapPost("/preview", async (HostMetricRetentionPreviewRequest input, HttpContext context, HostOwnedMetricRetentionSettings settings, CancellationToken token) =>
        {
            if (!HostMetricRetentionLimits.IsValid(input.RawDays, input.RollupDays)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["days"] = ["Invalid retention period."] });
            return await ApiMutation.ExecuteAsync(context, async actor => await settings.PreviewAsync(input, actor, token) is { } preview
                ? Results.Ok(preview) : Results.Conflict(), allowApiCredential: false);
        }).Produces<HostMetricRetentionPreview>().Produces(409).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403);
        admin.MapPut("", async (HostMetricRetentionInput input, HttpContext context, HostOwnedMetricRetentionSettings settings, CancellationToken token) =>
        {
            if (!HostMetricRetentionLimits.IsValid(input.RawDays, input.RollupDays)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["days"] = ["Invalid retention period."] });
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                try { return await settings.UpdateAsync(input, actor, token) is { } saved ? Results.Ok(saved) : Results.Conflict(); }
                catch (ArgumentException) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["preview"] = ["A current preview is required."] }); }
            }, allowApiCredential: false);
        }).Produces<HostMetricRetentionSnapshot>().Produces(409).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403);
    }
}
