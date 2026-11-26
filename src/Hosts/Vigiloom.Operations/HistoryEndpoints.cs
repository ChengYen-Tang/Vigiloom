using Vigiloom.Contracts.Notifications;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Application.Retention;

namespace Vigiloom.Operations;

internal static class HistoryEndpoints
{
    internal static void MapHistoryEndpoints(this WebApplication app)
    {
        var history = app.MapGroup("/internal/v1/history");
        history.MapGet("/devices/{deviceId:guid}/baseline-exceptions", (Guid deviceId, BaselineExceptionReader reader, CancellationToken token) =>
            reader.GetRecentAsync(deviceId, token));
        history.MapGet("/notifications", async (int? page, string? source, DateOnly? start, DateOnly? end, NotificationReader reader, CancellationToken token) =>
        {
            var query = new NotificationQuery(page ?? 1, source, start, end);
            return Valid(query) ? Results.Ok(await reader.GetRecentAsync(query, token)) : Results.BadRequest();
        });
        history.MapGet("/alerts", async (int? page, string? source, DateOnly? start, DateOnly? end, NotificationReader reader, CancellationToken token) =>
        {
            var query = new NotificationQuery(page ?? 1, source, start, end);
            return Valid(query) ? Results.Ok(await reader.GetEpisodesAsync(query, token)) : Results.BadRequest();
        });
        history.MapGet("/retention", async (AuditRetentionSettings settings, CancellationToken token) =>
            Results.Ok(await settings.GetAsync(token)));
        history.MapPost("/retention/preview", async (RetentionPreviewChange input, AuditRetentionSettings settings, CancellationToken token) =>
        {
            if (input.ActorId == Guid.Empty || input.Settings is null || !AuditRetentionLimits.IsValid(input.Settings.Days)) return Results.BadRequest();
            return await settings.PreviewAsync(input.Settings, input.ActorId, token) is { } preview ? Results.Ok(preview) : Results.Conflict();
        });
        history.MapPut("/retention", async (RetentionChange input, AuditRetentionSettings settings, CancellationToken token) =>
        {
            if (input.ActorId == Guid.Empty || input.Settings is null || !AuditRetentionLimits.IsValid(input.Settings.Days)) return Results.BadRequest();
            try { return await settings.UpdateAsync(input.Settings, input.ActorId, token) is { } result ? Results.Ok(result) : Results.Conflict(); }
            catch (ArgumentException) { return Results.BadRequest(); }
        });
    }

    private static bool Valid(NotificationQuery query) =>
        query.Page is >= 1 and <= 1000 && query.Source is not { Length: > 64 } && !(query.Start > query.End);
}
