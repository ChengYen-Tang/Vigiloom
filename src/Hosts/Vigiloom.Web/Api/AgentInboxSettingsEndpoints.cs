using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class AgentInboxSettingsEndpoints
{
    public static void MapAgentInboxSettingsEndpoints(this WebApplication app)
    {
        var admin = app.MapGroup("/api/v1/settings/agent-inbox")
            .RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client");
        admin.MapGet("/", async (AgentInboxSettings settings, CancellationToken token) => Results.Ok(await settings.GetAsync(token)))
            .Produces<AgentInboxSettingsSnapshot>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithDescription("Requires an Administrator session. Last cleanup time is the last batch that deleted receipts.");
        admin.MapPost("/cutover", async (AgentInboxCutoverRequest input, AgentInboxSettings settings, HttpContext context, CancellationToken token) =>
        {
            if (!input.ConfirmedReceiversStoppedAndDrained)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["confirmation"] = ["All old receiver processes must be stopped and in-flight transactions drained."] });
            return await ApiMutation.ExecuteAsync(context, async actor => await settings.ActivateCleanupAsync(input.Version, actor, token)
                ? Results.NoContent() : Results.Conflict(), allowApiCredential: false);
        }).Produces(204).Produces(409).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithDescription("Requires an Administrator session and antiforgery token. Confirm that all old receiver processes and their in-flight transactions have stopped before enabling cleanup.");
        admin.MapPost("/preview", async (AgentInboxPreviewRequest input, AgentInboxSettings settings, HttpContext context, CancellationToken token) =>
        {
            if (!AgentInboxPolicyLimits.IsValid(input.ReplaySeconds, input.RetentionDays)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["policy"] = ["Invalid inbox policy."] });
            return await ApiMutation.ExecuteAsync(context, async actor => await settings.PreviewAsync(input.ReplaySeconds, input.RetentionDays, input.Version, actor, token) is { } preview
                ? Results.Ok(preview) : Results.Conflict(), allowApiCredential: false);
        }).Produces<AgentInboxSettingsPreview>().Produces(409).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithDescription("Requires an Administrator session and antiforgery token. Returns 409 if the policy changed or cleanup is not ready.");
        admin.MapPut("/", async (AgentInboxSettingsUpdate input, AgentInboxSettings settings, HttpContext context, CancellationToken token) =>
        {
            if (!AgentInboxPolicyLimits.IsValid(input.ReplaySeconds, input.RetentionDays)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["policy"] = ["Invalid inbox policy."] });
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                try { return await settings.UpdateAsync(input.ReplaySeconds, input.RetentionDays, input.Version, input.ConfirmationToken, actor, token) ? Results.NoContent() : Results.Conflict(); }
                catch (ArgumentException) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["preview"] = ["A current preview is required to shorten inbox obligations."] }); }
                catch (AgentInboxNotReadyException) { return Results.Conflict(); }
            }, allowApiCredential: false);
        }).Produces(204).Produces(409).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithDescription("Requires an Administrator session and antiforgery token. Shortening requires a current preview; 409 means the policy changed or cleanup is not ready.");
    }
}
