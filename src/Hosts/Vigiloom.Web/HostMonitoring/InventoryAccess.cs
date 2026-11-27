using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class InventoryAccess(IServiceProvider services, IHttpClientFactory clients, IConfiguration configuration)
{
    private bool Remote => !string.IsNullOrWhiteSpace(configuration["Services:Security:Url"]);
    private static string Path(Guid deviceId) => $"internal/v1/devices/{deviceId:D}/inventory/";

    public Task<DeviceInventoryView> GetAsync(Guid deviceId, Guid? agentId, CancellationToken token) => Remote
        ? ReadAsync<DeviceInventoryView>(agentId is { } id ? QueryHelpers.AddQueryString(Path(deviceId), "agentId", id.ToString("D")) : Path(deviceId), token)
        : services.GetRequiredService<InventoryQuery>().GetAsync(deviceId, agentId, token);

    public Task<InventoryChangePage> GetChangesAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, InventorySource? source, string? cursor, CancellationToken token) => Remote
        ? ReadAsync<InventoryChangePage>(QueryHelpers.AddQueryString(Path(deviceId) + "changes", new Dictionary<string, string?>
        {
            ["from"] = from.ToString("O"), ["until"] = until.ToString("O"),
            ["source"] = source is null ? null : ((int)source.Value).ToString(), ["cursor"] = cursor
        }), token)
        : services.GetRequiredService<InventoryChangeQuery>().GetAsync(deviceId, from, until, source, cursor, token);

    public async Task<InventorySnapshotChunkView?> GetChunkAsync(Guid deviceId, Guid agentId, InventorySource source, Guid snapshotId, int chunkIndex, CancellationToken token)
    {
        if (!Remote) return await services.GetRequiredService<InventoryQuery>().GetChunkAsync(deviceId, agentId, source, snapshotId, chunkIndex, token);
        var path = QueryHelpers.AddQueryString(Path(deviceId) + $"{(int)source}/snapshots/{snapshotId:D}/chunks/{chunkIndex}", "agentId", agentId.ToString("D"));
        using var response = await clients.CreateClient("security").GetAsync(path, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid inventory query.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<InventorySnapshotChunkView>(cancellationToken: token)
            ?? throw new InvalidOperationException("Security returned an empty inventory chunk.");
    }

    private async Task<T> ReadAsync<T>(string path, CancellationToken token)
    {
        using var response = await clients.CreateClient("security").GetAsync(path, token);
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid inventory query.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("Security returned an empty inventory response.");
    }
}
