using System.Net.Http.Json;
using System.Security.Claims;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Security;
using Vigiloom.Contracts.Operations;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Modules.SecurityAnalysis.Application.Detection;

namespace Vigiloom.Security;

internal static class YaraEndpoints
{
    internal static void MapPublicYara(this WebApplication app)
    {
        const string root = "/api/v1/devices/{deviceId:guid}/yara-scans";
        app.MapGet(root, async (Guid deviceId, YaraScans scans, IHttpClientFactory clients, TimeProvider clock, HttpContext context, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor) || actor == Guid.Empty) return Results.Forbid();
            try
            {
                var jobs = await scans.ListAsync(deviceId, token);
                var entry = new OperationAudit(actor, deviceId, clock.GetUtcNow().ToUniversalTime(), new EventEvidenceAccessed("yara-list", deviceId, deviceId));
                using var response = await clients.CreateClient("operations").PostAsJsonAsync("internal/v1/audit/event-access", entry, token);
                response.EnsureSuccessStatusCode();
                return Results.Ok(jobs);
            }
            catch (ArgumentException) { return Results.BadRequest(); }
        }).RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client")
            .Produces<IReadOnlyList<YaraScanJob>>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403);
        app.MapPost(root, async (Guid deviceId, YaraScanRequest input, ResourceDevices resources, YaraScans scans, HttpContext context, CancellationToken token) =>
        {
            try
            {
                return await ApiMutation.ExecuteAsync(context, async actor =>
                {
                    var owner = await resources.ResolveAsync(deviceId, token);
                    if (owner is null) return Results.NotFound();
                    var identity = new RegisteredAgentIdentity(owner.AgentId, owner.DeviceId, owner.DeviceGeneration);
                    return Results.Ok(await scans.CreateAsync(identity, input, actor, token));
                }, allowApiCredential: false);
            }
            catch (ArgumentException) { return Results.BadRequest(); }
        }).RequireAuthorization("Administrator").RequireRateLimiting("api-client")
            .Produces<YaraScanJob>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);
        app.MapPost(root + "/{scanId:guid}/cancel", async (Guid deviceId, Guid scanId, YaraScans scans, HttpContext context, CancellationToken token) =>
        {
            try { return await ApiMutation.ExecuteAsync(context, async _ => await scans.CancelAsync(deviceId, scanId, token) ? Results.NoContent() : Results.NotFound(), allowApiCredential: false); }
            catch (ArgumentException) { return Results.BadRequest(); }
        }).RequireAuthorization("Administrator").RequireRateLimiting("api-client")
            .Produces(204).ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404);
    }
}
