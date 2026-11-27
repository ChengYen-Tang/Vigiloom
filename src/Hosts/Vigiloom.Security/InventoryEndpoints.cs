using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

namespace Vigiloom.Security;

internal static class InventoryEndpoints
{
    internal static void MapPublicInventory(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/devices/{deviceId:guid}/inventory")
            .RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client");
        group.MapGet("", async (Guid deviceId, HttpContext context, ResourceDevices resources, InventoryQuery query, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var owner = await resources.GetOwnershipAsync(deviceId, token);
            return owner is null ? Results.NotFound() : Results.Ok(await query.GetAsync(deviceId, owner.Registration?.Id, token));
        }).Produces<DeviceInventoryView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
        group.MapGet("/changes", async (Guid deviceId, DateTimeOffset from, DateTimeOffset until, int? source, string? cursor,
            HttpContext context, ResourceDevices resources, InventoryChangeQuery query, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (source is { } value && !Enum.IsDefined((InventorySource)value)) return Results.BadRequest();
            if (await resources.GetOwnershipAsync(deviceId, token) is null) return Results.NotFound();
            try { return Results.Ok(await query.GetAsync(deviceId, from, until, (InventorySource?)source, cursor, token)); }
            catch (ArgumentException) { return Results.BadRequest(); }
        }).Produces<InventoryChangePage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
        group.MapGet("/{source:int}/snapshots/{snapshotId:guid}/chunks/{chunkIndex:int}", async (Guid deviceId, int source, Guid snapshotId, int chunkIndex,
            HttpContext context, ResourceDevices resources, InventoryQuery query, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (!Enum.IsDefined((InventorySource)source) || snapshotId.Version != 7 || chunkIndex is < 0 or >= InventoryChunkCodec.MaximumChunks) return Results.BadRequest();
            var owner = await resources.GetOwnershipAsync(deviceId, token);
            if (owner?.Registration?.Id is not { } agentId) return Results.NotFound();
            var chunk = await query.GetChunkAsync(deviceId, agentId, (InventorySource)source, snapshotId, chunkIndex, token);
            return chunk is null ? Results.NotFound() : Results.Ok(chunk);
        }).Produces<InventorySnapshotChunkView>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429);
    }
}
