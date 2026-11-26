using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Retention;
using Vigiloom.Web.Authentication;
using Vigiloom.Web.ServiceDetails;

namespace Vigiloom.Web.Api;

internal static class TelemetryRetentionEndpoints
{
    public static void MapTelemetryRetentionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/settings/telemetry/retention", async (TelemetryRetentionAccess settings, CancellationToken token) => Results.Ok(await settings.GetAsync(token)))
            .RequireAuthorization(AccessPolicies.Administrator).Produces<TelemetryRetentionSnapshot>().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Get application telemetry retention settings");
        app.MapPost("/api/v1/settings/telemetry/retention/preview", async (TelemetryRetentionPreviewRequest input, HttpContext context, TelemetryRetentionAccess settings, CancellationToken token) =>
        {
            if (!TelemetryRetentionLimits.IsValid(input.LogDays, input.TraceDays)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["days"] = ["Invalid retention period."] });
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                try { return await settings.PreviewAsync(input, actor, token) is { } preview ? Results.Ok(preview) : Results.Conflict(); }
                catch (ArgumentException) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["days"] = ["Invalid retention period."] }); }
            }, allowApiCredential: false);
        }).RequireAuthorization(AccessPolicies.Administrator).Produces<TelemetryRetentionPreview>().Produces(409).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Preview application telemetry retention changes");
        app.MapPut("/api/v1/settings/telemetry/retention", async (TelemetryRetentionInput input, HttpContext context, TelemetryRetentionAccess settings, CancellationToken token) =>
        {
            if (!TelemetryRetentionLimits.IsValid(input.LogDays, input.TraceDays)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["days"] = ["Invalid retention period."] });
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                try { return await settings.UpdateAsync(input, actor, token) is { } saved ? Results.Ok(saved) : Results.Conflict(); }
                catch (ArgumentException) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["preview"] = ["A current preview is required."] }); }
            }, allowApiCredential: false);
        }).RequireAuthorization(AccessPolicies.Administrator).Produces<TelemetryRetentionSnapshot>().Produces(409).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Update application telemetry retention settings");
    }
}
