using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry.Retention;

namespace Vigiloom.Observability;

internal static class RetentionEndpoints
{
    internal static void MapRetentionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/internal/v1/telemetry-retention");
        group.MapGet("/", async (TelemetryRetentionSettings settings, CancellationToken token) => Results.Ok(await settings.GetAsync(token)));
        group.MapPost("/preview", async (TelemetryRetentionPreviewCommand command, TelemetryRetentionSettings settings, CancellationToken token) =>
        {
            try { return await settings.PreviewAsync(command.Input, command.ActorId, token) is { } result ? Results.Ok(result) : Results.Conflict(); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
        group.MapPut("/", async (TelemetryRetentionUpdateCommand command, TelemetryRetentionSettings settings, CancellationToken token) =>
        {
            try { return await settings.UpdateAsync(command.Input, command.ActorId, token) is { } result ? Results.Ok(result) : Results.Conflict(); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
    }
}
