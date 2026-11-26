using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Domain;

namespace Vigiloom.HostMonitoring;

internal static class HostInboxEndpoints
{
    public static void MapHostInboxEndpoints(this WebApplication app)
    {
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
