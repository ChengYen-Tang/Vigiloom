using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Application.Integrity;

namespace Vigiloom.Security;

internal static class IntegrityEndpoints
{
    internal static void MapPublicIntegrity(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/devices/{deviceId:guid}/integrity")
            .RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client");
        group.MapGet("", async (Guid deviceId, HttpContext context, ResourceDevices resources, IntegrityQuery query, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var owner = await resources.GetOwnershipAsync(deviceId, token);
            return owner is null ? Results.NotFound() : Results.Ok(await query.GetAsync(deviceId, owner.Registration?.Id, token));
        }).Produces<DeviceIntegrityView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
        group.MapGet("/changes", async (Guid deviceId, DateTimeOffset from, DateTimeOffset until, Guid? scopeId, string? cursor,
            HttpContext context, ResourceDevices resources, IntegrityChangeQuery query, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (await resources.GetOwnershipAsync(deviceId, token) is null) return Results.NotFound();
            try { return Results.Ok(await query.GetAsync(deviceId, from, until, scopeId, cursor, token)); }
            catch (ArgumentException) { return Results.BadRequest(); }
        }).Produces<IntegrityChangePage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
        group.MapGet("/scopes/{scopeId:guid}/snapshots/{snapshotId:guid}/chunks/{chunkIndex:int}",
            async (Guid deviceId, Guid scopeId, Guid snapshotId, int chunkIndex, HttpContext context,
                ResourceDevices resources, IntegrityQuery query, CancellationToken token) =>
            {
                context.Response.Headers.CacheControl = "private, no-store";
                if (scopeId.Version != 7 || snapshotId.Version != 7 || chunkIndex is < 0 or >= IntegrityChunkCodec.MaximumChunks)
                    return Results.BadRequest();
                var owner = await resources.GetOwnershipAsync(deviceId, token);
                if (owner?.Registration?.Id is not { } agentId) return Results.NotFound();
                var chunk = await query.GetChunkAsync(deviceId, agentId, scopeId, snapshotId, chunkIndex, token);
                return chunk is null ? Results.NotFound() : Results.Ok(chunk);
            }).Produces<IntegritySnapshotChunkView>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
    }
}
