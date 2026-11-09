using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Api;

internal static class SpeedTestEndpoints
{
    public static void MapSpeedTestEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/speed-tests", async (DateTimeOffset from, DateTimeOffset to, SpeedHistory history, CancellationToken token,
            string? search = null, string sort = "time", bool descending = true, int page = 1) =>
        {
            var query = new SpeedHistoryQuery(from, to, search, sort, descending, page);
            return SpeedHistory.Valid(query) ? Results.Ok(await history.QueryAsync(query, token)) : Results.ValidationProblem(new Dictionary<string, string[]> { ["range"] = ["Use a UTC range of at most 31 days and a valid sort/page."] });
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<SpeedHistoryPage>().ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithSummary("Query private speed-test history and bounded trend points");
        app.MapGet("/api/v1/speed-tests/{id:guid}", async (Guid id, SpeedHistory history, CancellationToken token) =>
            await history.GetAsync(id, token) is { } result ? Results.Ok(result) : Results.NotFound())
            .RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client").Produces<SpeedRunDetails>().ProducesProblem(404).ProducesProblem(401).ProducesProblem(403).ProducesProblem(429)
            .WithSummary("Get one private speed-test result");
        app.MapPost("/api/v1/speed-tests/{id:guid}/cancel", async (Guid id, SpeedQueue queue, HttpContext context, CancellationToken token) =>
            await ApiMutation.ExecuteAsync(context, async _ => await queue.CancelAsync(id, token) ? Results.NoContent() : Results.NotFound(), allowApiCredential: false))
            .RequireAuthorization(AccessPolicies.Administrator).Produces(204).ProducesProblem(404).ProducesProblem(401).ProducesProblem(403).ProducesProblem(400)
            .WithSummary("Cancel a queued or running speed test");
        app.MapGet("/api/v1/settings/speed-tests", async (SpeedSettings settings, CancellationToken token) => Results.Ok(await settings.GetAsync(token)))
            .RequireAuthorization(AccessPolicies.Administrator).Produces<SpeedSettingsSnapshot>().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Get speed-test schedule settings");
        app.MapPut("/api/v1/settings/speed-tests", async (SpeedSettingsInput input, SpeedSettings settings, HttpContext context, CancellationToken token) =>
        {
            if (!SpeedSettings.Valid(input)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["settings"] = ["Invalid speed-test settings."] });
            return await ApiMutation.ExecuteAsync(context, async actor => await settings.UpdateAsync(input, actor, token) is { } saved
                ? Results.Ok(saved) : Results.Conflict(), allowApiCredential: false);
        }).RequireAuthorization(AccessPolicies.Administrator).Produces<SpeedSettingsSnapshot>().Produces(409).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Update speed-test scheduling and limits");
        app.MapGet("/api/v1/settings/speed-retention", async (SpeedRetentionSettings settings, CancellationToken token) => Results.Ok(await settings.GetAsync(token)))
            .RequireAuthorization(AccessPolicies.Administrator).Produces<SpeedRetentionSnapshot>().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Get speed-test retention policy and cleanup progress");
        app.MapPost("/api/v1/settings/speed-retention/preview", async (SpeedRetentionPreviewRequest input, SpeedRetentionSettings settings, HttpContext context, CancellationToken token) =>
        {
            if (!SpeedRetentionLimits.IsValid(input.Days)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["days"] = ["Invalid retention period."] });
            return await ApiMutation.ExecuteAsync(context, async actor => await settings.PreviewAsync(input.Days, input.Version, actor, token) is { } preview
                ? Results.Ok(preview) : Results.Conflict(), allowApiCredential: false);
        }).RequireAuthorization(AccessPolicies.Administrator).Produces<SpeedRetentionPreview>().Produces(409).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Preview a speed-test retention policy change");
        app.MapPut("/api/v1/settings/speed-retention", async (SpeedRetentionInput input, SpeedRetentionSettings settings, HttpContext context, CancellationToken token) =>
        {
            if (!SpeedRetentionLimits.IsValid(input.Days)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["days"] = ["Invalid retention period."] });
            return await ApiMutation.ExecuteAsync(context, async actor =>
            {
                try { return await settings.UpdateAsync(input, actor, token) is { } saved ? Results.Ok(saved) : Results.Conflict(); }
                catch (ArgumentException) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["preview"] = ["A valid, current preview is required when reducing retention."] }); }
            }, allowApiCredential: false);
        }).RequireAuthorization(AccessPolicies.Administrator).Produces<SpeedRetentionSnapshot>().Produces(409).ProducesValidationProblem().ProducesProblem(401).ProducesProblem(403)
            .WithSummary("Update speed-test retention policy");
    }
}
