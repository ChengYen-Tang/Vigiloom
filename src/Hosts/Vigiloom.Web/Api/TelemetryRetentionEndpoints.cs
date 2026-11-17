using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Retention;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class TelemetryRetentionEndpoints
{
    public static void MapTelemetryRetentionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/settings/telemetry/retention", async (TelemetryRetentionSettings settings, CancellationToken token) => Results.Ok(await settings.GetAsync(token)))
            .RequireAuthorization(AccessPolicies.Administrator).Produces<TelemetryRetentionSnapshot>().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Get application log and trace retention settings");
        app.MapPost("/api/v1/settings/telemetry/retention/preview", async (TelemetryRetentionPreviewRequest input, HttpContext context, TelemetryRetentionSettings settings, CancellationToken token) =>
        {
            if (!TelemetryRetentionLimits.IsValid(input.LogDays, input.TraceDays)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["days"] = ["Invalid retention period."] });
            return await ApiMutation.ExecuteAsync(context, async actor => await settings.PreviewAsync(input, actor, token) is { } preview
                ? Results.Ok(preview) : Results.Conflict(), allowApiCredential: false);
        }).RequireAuthorization(AccessPolicies.Administrator).Produces<TelemetryRetentionPreview>().Produces(409).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Preview application log and trace retention changes");
        app.MapPut("/api/v1/settings/telemetry/retention", async (TelemetryRetentionInput input, HttpContext context, TelemetryRetentionSettings settings, CancellationToken token) =>
        {
            if (!TelemetryRetentionLimits.IsValid(input.LogDays, input.TraceDays)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["days"] = ["Invalid retention period."] });
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                try { return await settings.UpdateAsync(input, actor, token) is { } saved ? Results.Ok(saved) : Results.Conflict(); }
                catch (ArgumentException) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["preview"] = ["A current preview is required."] }); }
            }, allowApiCredential: false);
        }).RequireAuthorization(AccessPolicies.Administrator).Produces<TelemetryRetentionSnapshot>().Produces(409).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Update application log and trace retention settings");
    }
}
