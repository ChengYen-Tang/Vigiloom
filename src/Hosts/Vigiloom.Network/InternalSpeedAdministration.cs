using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

namespace Vigiloom.Network;

internal static class InternalSpeedAdministration
{
    public static void MapInternalSpeedAdministration(this WebApplication app)
    {
        app.MapGet("/internal/v1/speed/settings", async (SpeedSettings settings, CancellationToken token) =>
            Results.Ok(await settings.GetAsync(token)));
        app.MapPost("/internal/v1/speed/settings", async (SpeedSettingsCommand command, SpeedSettings settings, CancellationToken token) =>
        {
            if (command.Input is null || !SpeedSettings.Valid(command.Input) || command.Actor == Guid.Empty) return Results.BadRequest();
            var result = await settings.UpdateAsync(command.Input, command.Actor, token);
            return result is null ? Results.Conflict() : Results.Ok(result);
        });
        app.MapGet("/internal/v1/speed/retention", async (SpeedRetentionSettings settings, CancellationToken token) =>
            Results.Ok(await settings.GetAsync(token)));
        app.MapPost("/internal/v1/speed/retention/preview", async (SpeedRetentionPreviewCommand command, SpeedRetentionSettings settings, CancellationToken token) =>
        {
            if (!SpeedRetentionLimits.IsValid(command.Days) || command.Actor == Guid.Empty) return Results.BadRequest();
            var result = await settings.PreviewAsync(command.Days, command.Version, command.Actor, token);
            return result is null ? Results.Conflict() : Results.Ok(result);
        });
        app.MapPost("/internal/v1/speed/retention", async (SpeedRetentionCommand command, SpeedRetentionSettings settings, CancellationToken token) =>
        {
            if (command.Input is null || !SpeedRetentionLimits.IsValid(command.Input.Days) || command.Actor == Guid.Empty) return Results.BadRequest();
            try
            {
                var result = await settings.UpdateAsync(command.Input, command.Actor, token);
                return result is null ? Results.Conflict() : Results.Ok(result);
            }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        app.MapPost("/internal/v1/speed/{id:guid}/cancel", async (Guid id, SpeedQueue queue, CancellationToken token) =>
            await queue.CancelAsync(id, token) ? Results.NoContent() : Results.NotFound());
    }
}
