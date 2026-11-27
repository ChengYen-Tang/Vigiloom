using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Contracts.Agent;
using Vigiloom.Infrastructure.Authentication;

namespace Vigiloom.HostMonitoring;

internal static class HostInboxEndpoints
{
    public static void MapHostInboxEndpoints(this WebApplication app)
    {
        var inbox = app.MapGroup("/api/v1/settings/agent-inbox").RequireAuthorization("Administrator").RequireRateLimiting("api-client");
        inbox.MapGet("/", async (HostInboxSettings settings, CancellationToken token) => Results.Ok(await settings.GetAsync(token)));
        inbox.MapPost("/cutover", async (AgentInboxCutoverRequest input, HostInboxSettings settings, HttpContext context, CancellationToken token) =>
        {
            if (!input.ConfirmedReceiversStoppedAndDrained)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["confirmation"] = ["All old receiver processes must be stopped and in-flight transactions drained."] });
            return await ApiMutation.ExecuteAsync(context, async actor => await settings.ActivateCleanupAsync(input.Version, actor, token)
                ? Results.NoContent() : Results.Conflict(), allowApiCredential: false);
        });
        inbox.MapPost("/preview", async (AgentInboxPreviewRequest input, HostInboxSettings settings, HttpContext context, CancellationToken token) =>
        {
            if (!AgentInboxPolicyLimits.IsValid(input.ReplaySeconds, input.RetentionDays))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["policy"] = ["Invalid inbox policy."] });
            return await ApiMutation.ExecuteAsync(context, async actor => await settings.PreviewAsync(input.ReplaySeconds, input.RetentionDays, input.Version, actor, token) is { } preview
                ? Results.Ok(preview) : Results.Conflict(), allowApiCredential: false);
        });
        inbox.MapPut("/", async (AgentInboxSettingsUpdate input, HostInboxSettings settings, HttpContext context, CancellationToken token) =>
        {
            if (!AgentInboxPolicyLimits.IsValid(input.ReplaySeconds, input.RetentionDays))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["policy"] = ["Invalid inbox policy."] });
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                try { return await settings.UpdateAsync(input.ReplaySeconds, input.RetentionDays, input.Version, input.ConfirmationToken, actor, token) ? Results.NoContent() : Results.Conflict(); }
                catch (ArgumentException) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["preview"] = ["A current preview is required to shorten inbox obligations."] }); }
                catch (AgentInboxNotReadyException) { return Results.Conflict(); }
            }, allowApiCredential: false);
        });
        var replay = app.MapGroup("/api/v1/agent-management/replay-policy").RequireAuthorization("Administrator").RequireRateLimiting("api-client");
        replay.MapGet("/", async (HttpContext context, HostReplayPolicy policy, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await policy.GetAsync(token));
        });
        replay.MapPut("/", async (AgentReplayPolicyUpdate input, HttpContext context, HostReplayPolicy policy, CancellationToken token) =>
        {
            if (input.Target is not (AgentReplayPhase.Grace or AgentReplayPhase.Enforced) || !input.ConfirmedReceiversUpgraded)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["policy"] = ["請先確認所有接收節點已更新，並選擇有效的政策階段。"] });
            return await ApiMutation.ExecuteAsync(context, async actor => await policy.TransitionAsync(input.Target, input.Version, actor, token) is { } saved
                ? Results.Ok(saved) : Results.Problem(statusCode: 409, title: "政策狀態已變更或過渡期尚未結束，請重新整理。"), allowApiCredential: false);
        });
        app.MapGet("/internal/v1/agent-inbox", (HostInboxSettings settings, CancellationToken token) => settings.GetAsync(token));
        app.MapPost("/internal/v1/agent-inbox/cutover", async (InboxCutoverCommand command, HostInboxSettings settings, CancellationToken token) =>
        {
            try { return await settings.ActivateCleanupAsync(command.Version, command.Actor, token) ? Results.NoContent() : Results.Conflict(); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        app.MapPost("/internal/v1/agent-inbox/preview", async (InboxPreviewCommand command, HostInboxSettings settings, CancellationToken token) =>
        {
            try
            {
                var input = command.Input;
                var preview = await settings.PreviewAsync(input.ReplaySeconds, input.RetentionDays, input.Version, command.Actor, token);
                return preview is null ? Results.Conflict() : Results.Ok(preview);
            }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        app.MapPost("/internal/v1/agent-inbox/update", async (InboxUpdateCommand command, HostInboxSettings settings, CancellationToken token) =>
        {
            try
            {
                var input = command.Input;
                return await settings.UpdateAsync(input.ReplaySeconds, input.RetentionDays, input.Version, input.ConfirmationToken, command.Actor, token)
                    ? Results.NoContent() : Results.Conflict();
            }
            catch (ArgumentException) { return Results.BadRequest(); }
            catch (AgentInboxNotReadyException) { return Results.Conflict(); }
        });
        app.MapGet("/internal/v1/agent-replay-policy", (HostReplayPolicy policy, CancellationToken token) => policy.GetAsync(token));
        app.MapPost("/internal/v1/agent-replay-policy", async (ReplayPolicyCommand command, HostReplayPolicy policy, CancellationToken token) =>
        {
            try
            {
                var result = await policy.TransitionAsync(command.Input.Target, command.Input.Version, command.Actor, token);
                return result is null ? Results.Conflict() : Results.Ok(result);
            }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
    }
}
