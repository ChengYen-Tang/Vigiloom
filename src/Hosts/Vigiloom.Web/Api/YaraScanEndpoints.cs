using System.Security.Claims;
using Vigiloom.Contracts.Agent.Security;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Web.Authentication;
using Vigiloom.Web.Events;

namespace Vigiloom.Web.Api;

internal static class YaraScanEndpoints
{
    public static void MapYaraScanEndpoints(this WebApplication app)
    {
        app.MapGet("/api/v1/devices/{deviceId:guid}/yara-scans", async (Guid deviceId, YaraScans scans, EventTimelineAudit audit, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor) || actor == Guid.Empty) return Results.Forbid();
            try
            {
                var jobs = await scans.ListAsync(deviceId, token);
                await audit.RecordEvidenceAsync(actor, new EventEvidenceAccessed("yara-list", deviceId, deviceId), token);
                return Results.Ok(jobs);
            }
            catch (ArgumentException) { return Results.BadRequest(); }
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<IReadOnlyList<YaraScanJob>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);

        app.MapPost("/api/v1/devices/{deviceId:guid}/yara-scans", async (Guid deviceId, YaraScanRequest input,
            YaraScans scans, HttpContext context, CancellationToken token) =>
        {
            try
            {
                return await ApiMutation.ExecuteAsync(context, async actor =>
                    await scans.CreateAsync(deviceId, input, actor, token) is { } job ? Results.Ok(job) : Results.NotFound(), allowApiCredential: false);
            }
            catch (ArgumentException) { return Results.BadRequest(); }
        }).RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client")
            .Produces<YaraScanJob>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);

        app.MapPost("/api/v1/devices/{deviceId:guid}/yara-scans/{scanId:guid}/cancel", async (Guid deviceId, Guid scanId,
            YaraScans scans, HttpContext context, CancellationToken token) =>
        {
            try { return await ApiMutation.ExecuteAsync(context, async _ => await scans.CancelAsync(deviceId, scanId, token) ? Results.NoContent() : Results.NotFound(), allowApiCredential: false); }
            catch (ArgumentException) { return Results.BadRequest(); }
        }).RequireAuthorization(AccessPolicies.Administrator).RequireRateLimiting("api-client")
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);

        app.MapGet("/api/v1/agent/yara-scans/next", async (YaraScans scans, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var result = await scans.NextAsync(AgentEndpoints.Bearer(context), token);
            return !result.Authenticated ? Results.Unauthorized() : result.Job is null ? Results.NoContent() : Results.Ok(result.Job);
        }).RequireAuthorization("Agent").RequireRateLimiting("agent-transport").WithMetadata(AgentTransportEndpoint.Instance)
            .Produces<YaraScanJob>().Produces(204).ProducesProblem(401).ProducesProblem(403);

    }
}
