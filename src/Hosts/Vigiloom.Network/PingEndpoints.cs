using Vigiloom.Contracts.Network;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;

using Vigiloom.Infrastructure.Authentication;

namespace Vigiloom.Network;

internal static class PingEndpoints
{
    public static void MapPingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/devices/{id:guid}/ping/history", async (Guid id, DateTimeOffset from, DateTimeOffset to,
            string location, ResourceDevices devices, PingHistoryReader history, HttpContext context, CancellationToken token,
            string? target = null, int page = 1, bool descending = true) =>
        {
            var query = new PingHistoryQuery(id, from, to, target, location, page, descending);
            if (!PingHistoryReader.Valid(query)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["range"] = ["Use a five-minute-aligned UTC range of at most seven days and a valid target and location."] });
            if (await devices.GetAsync(id, token) is null) return Results.NotFound();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await history.QueryAsync(query, token));
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<PingHistoryPage>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429)
            .WithSummary("Query authorized Ping samples and five-minute statistics");
        app.MapGet("/api/v1/devices/{id:guid}/ping", async (Guid id, ResourceDevices devices, PingStore store, CancellationToken token) =>
        {
            var device = await devices.GetAsync(id, token);
            if (device is null) return Results.NotFound();
            if (device.Archived || !device.Enabled) return Results.Ok(new DevicePingStatus(id, []));
            return Results.Ok(await store.GetStatusAsync(id, device.AdditionalTargets.Prepend(device.Address).ToArray(), device.Version, token));
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<DevicePingStatus>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429)
            .WithSummary("Get the latest Ping result for each device target");
        app.MapGet("/api/v1/settings/ping", async (PingSettings settings, CancellationToken token) => Results.Ok(await settings.GetAsync(token)))
            .RequireAuthorization("Administrator").Produces<PingSettingsSnapshot>().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Get central Ping settings");
        app.MapPut("/api/v1/settings/ping", async (PingSettingsInput input, HttpContext context, PingSettings settings, CancellationToken token) =>
        {
            if (!PingSettings.Valid(input)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["settings"] = ["Ping 設定值超出允許範圍。"] });
            return await ApiMutation.ExecuteAsync(context, async actor => await settings.UpdateAsync(input, actor, token) is { } saved
                ? Results.Ok(saved) : Results.Problem(statusCode: 409, title: "Ping settings have changed; reload and try again."), allowApiCredential: false);
        }).RequireAuthorization("Administrator").Produces<PingSettingsSnapshot>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(409)
            .WithSummary("Update central Ping settings");
        app.MapGet("/api/v1/settings/ping/retention", async (PingRetentionSettings settings, CancellationToken token) => Results.Ok(await settings.GetAsync(token)))
            .RequireAuthorization("Administrator").Produces<PingRetentionSnapshot>().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Get Ping retention settings");
        app.MapPost("/api/v1/settings/ping/retention/preview", async (PingRetentionPreviewRequest input, HttpContext context, PingRetentionSettings settings, CancellationToken token) =>
        {
            if (!PingRetentionLimits.IsValid(input.RawDays, input.RollupDays)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["days"] = ["Invalid retention period."] });
            return await ApiMutation.ExecuteAsync(context, async actor => await settings.PreviewAsync(input, actor, token) is { } preview
                ? Results.Ok(preview) : Results.Problem(statusCode: 409, title: "Ping settings have changed; reload and try again."), allowApiCredential: false);
        }).RequireAuthorization("Administrator").Produces<PingRetentionPreview>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(409)
            .WithSummary("Preview Ping retention changes");
        app.MapPut("/api/v1/settings/ping/retention", async (PingRetentionInput input, HttpContext context, PingRetentionSettings settings, CancellationToken token) =>
        {
            if (!PingRetentionLimits.IsValid(input.RawDays, input.RollupDays)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["days"] = ["Invalid retention period."] });
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                try { return await settings.UpdateAsync(input, actor, token) is { } saved ? Results.Ok(saved) : Results.Problem(statusCode: 409, title: "Ping settings have changed; reload and try again."); }
                catch (ArgumentException) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["preview"] = ["A current preview is required."] }); }
            }, allowApiCredential: false);
        }).RequireAuthorization("Administrator").Produces<PingRetentionSnapshot>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(409)
            .WithSummary("Update Ping retention settings");
    }
}
