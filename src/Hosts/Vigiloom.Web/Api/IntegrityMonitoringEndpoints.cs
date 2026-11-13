using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.SecurityAnalysis.Application.Integrity;

namespace Vigiloom.Web.Api;

internal static class IntegrityMonitoringEndpoints
{
    public static void MapIntegrityMonitoringEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/devices/{deviceId:guid}/integrity")
            .RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client");
        group.MapGet("", async (Guid deviceId, HttpContext context, DeviceInventoryAgent ownership,
            IntegrityQuery query, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var resolved = await ownership.ResolveAsync(deviceId, token);
            return resolved.Exists ? Results.Ok(await query.GetAsync(deviceId, resolved.AgentId, token)) : Results.NotFound();
        }).Produces<DeviceIntegrityView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);

        group.MapGet("/settings-status", async (Guid deviceId, HttpContext context, AgentIntegritySettingsMonitoring monitoring, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            return await monitoring.GetAsync(deviceId, token) is { } status ? Results.Ok(status) : Results.NotFound();
        }).Produces<IntegritySettingsStatus>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);

        group.MapGet("/changes", async (Guid deviceId, DateTimeOffset from, DateTimeOffset until, Guid? scopeId,
            string? cursor, HttpContext context, DeviceInventoryAgent ownership, IntegrityChangeQuery query, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var resolved = await ownership.ResolveAsync(deviceId, token);
            if (!resolved.Exists) return Results.NotFound();
            try { return Results.Ok(await query.GetAsync(deviceId, from, until, scopeId, cursor, token)); }
            catch (ArgumentException) { return Results.BadRequest(); }
        }).Produces<IntegrityChangePage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);

        group.MapGet("/scopes/{scopeId:guid}/snapshots/{snapshotId:guid}/chunks/{chunkIndex:int}",
            async (Guid deviceId, Guid scopeId, Guid snapshotId, int chunkIndex, HttpContext context,
                DeviceInventoryAgent ownership, IntegrityQuery query, CancellationToken token) =>
            {
                context.Response.Headers.CacheControl = "private, no-store";
                if (scopeId.Version != 7 || snapshotId.Version != 7 || chunkIndex is < 0 or >= IntegrityChunkCodec.MaximumChunks)
                    return Results.BadRequest();
                var resolved = await ownership.ResolveAsync(deviceId, token);
                if (!resolved.Exists || resolved.AgentId is not { } agentId) return Results.NotFound();
                var chunk = await query.GetChunkAsync(deviceId, agentId, scopeId, snapshotId, chunkIndex, token);
                return chunk is null ? Results.NotFound() : Results.Ok(chunk);
            }).Produces<IntegritySnapshotChunkView>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
    }
}
