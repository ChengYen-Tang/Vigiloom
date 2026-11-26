using Vigiloom.Contracts.Network;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;

namespace Vigiloom.Network;

internal static class InternalPingAdministration
{
    public static void MapInternalPingAdministration(this WebApplication app)
    {
        app.MapGet("/internal/v1/ping/settings", async (PingSettings settings, CancellationToken token) => Results.Ok(await settings.GetAsync(token)));
        app.MapPost("/internal/v1/ping/settings", async (PingSettingsCommand command, PingSettings settings, CancellationToken token) =>
        {
            if (command.Input is null || !PingSettings.Valid(command.Input) || command.Actor == Guid.Empty) return Results.BadRequest();
            var result = await settings.UpdateAsync(command.Input, command.Actor, token);
            return result is null ? Results.Conflict() : Results.Ok(result);
        });
        app.MapGet("/internal/v1/ping/retention", async (PingRetentionSettings settings, CancellationToken token) => Results.Ok(await settings.GetAsync(token)));
        app.MapPost("/internal/v1/ping/retention/preview", async (PingRetentionPreviewCommand command, PingRetentionSettings settings, CancellationToken token) =>
        {
            if (command.Input is null || !PingRetentionLimits.IsValid(command.Input.RawDays, command.Input.RollupDays) || command.Actor == Guid.Empty) return Results.BadRequest();
            var result = await settings.PreviewAsync(command.Input, command.Actor, token);
            return result is null ? Results.Conflict() : Results.Ok(result);
        });
        app.MapPost("/internal/v1/ping/retention", async (PingRetentionCommand command, PingRetentionSettings settings, CancellationToken token) =>
        {
            if (command.Input is null || !PingRetentionLimits.IsValid(command.Input.RawDays, command.Input.RollupDays) || command.Actor == Guid.Empty) return Results.BadRequest();
            try
            {
                var result = await settings.UpdateAsync(command.Input, command.Actor, token);
                return result is null ? Results.Conflict() : Results.Ok(result);
            }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
    }
}
