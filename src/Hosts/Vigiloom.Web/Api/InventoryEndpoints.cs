using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

namespace Vigiloom.Web.Api;

internal static class InventoryEndpoints
{
    public static void MapInventoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/devices/{deviceId:guid}/inventory")
            .RequireAuthorization("MonitoringApiRead").RequireRateLimiting("api-client");
        group.MapGet("", async (Guid deviceId, HttpContext context, DeviceInventoryAgent ownership,
            InventoryQuery inventory, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            var resolved = await ownership.ResolveAsync(deviceId, token);
            return resolved.Exists ? Results.Ok(await inventory.GetAsync(deviceId, resolved.AgentId, token)) : Results.NotFound();
        }).Produces<DeviceInventoryView>().ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(429)
            .WithSummary("Read the most recent Agent's private inventory source status");

        group.MapGet("/changes", async (Guid deviceId, DateTimeOffset from, DateTimeOffset until, int? source,
            string? cursor, HttpContext context, DeviceInventoryAgent ownership, InventoryChangeQuery changes, CancellationToken token) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            if (source is { } value && !Enum.IsDefined((InventorySource)value)) return Results.BadRequest();
            var resolved = await ownership.ResolveAsync(deviceId, token);
            if (!resolved.Exists) return Results.NotFound();
            try { return Results.Ok(await changes.GetAsync(deviceId, from, until, (InventorySource?)source, cursor, token)); }
            catch (ArgumentException) { return Results.BadRequest(); }
        }).Produces<InventoryChangePage>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(429).WithSummary("Read a bounded page of private inventory changes");

        group.MapGet("/{source:int}/snapshots/{snapshotId:guid}/chunks/{chunkIndex:int}",
            async (Guid deviceId, int source, Guid snapshotId, int chunkIndex, HttpContext context,
                DeviceInventoryAgent ownership, InventoryQuery inventory, CancellationToken token) =>
            {
                context.Response.Headers.CacheControl = "private, no-store";
                if (!Enum.IsDefined((InventorySource)source) || snapshotId.Version != 7 ||
                    chunkIndex is < 0 or >= InventoryChunkCodec.MaximumChunks) return Results.BadRequest();
                var resolved = await ownership.ResolveAsync(deviceId, token);
                if (!resolved.Exists || resolved.AgentId is not { } agentId) return Results.NotFound();
                var chunk = await inventory.GetChunkAsync(deviceId, agentId, (InventorySource)source, snapshotId, chunkIndex, token);
                return chunk is null ? Results.NotFound() : Results.Ok(chunk);
            }).Produces<InventorySnapshotChunkView>().ProducesProblem(400).ProducesProblem(401).ProducesProblem(403)
            .ProducesProblem(404).ProducesProblem(429).WithSummary("Read a bounded chunk of the current private inventory snapshot");
    }
}
