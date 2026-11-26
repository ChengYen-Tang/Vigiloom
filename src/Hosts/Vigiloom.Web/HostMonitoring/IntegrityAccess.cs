using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Application.Integrity;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class IntegrityAccess(IntegrityQuery legacy, IntegrityChangeQuery legacyChanges, IHttpClientFactory clients, IConfiguration configuration)
{
    private bool Remote => !string.IsNullOrWhiteSpace(configuration["Services:Security:Url"]);
    private static string Path(Guid deviceId) => $"internal/v1/devices/{deviceId:D}/integrity/";

    public Task<DeviceIntegrityView> GetAsync(Guid deviceId, Guid? agentId, CancellationToken token) => Remote
        ? ReadAsync<DeviceIntegrityView>(agentId is { } id ? QueryHelpers.AddQueryString(Path(deviceId), "agentId", id.ToString("D")) : Path(deviceId), token)
        : legacy.GetAsync(deviceId, agentId, token);

    public Task<IntegrityChangePage> GetChangesAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, Guid? scopeId, string? cursor, CancellationToken token) => Remote
        ? ReadAsync<IntegrityChangePage>(QueryHelpers.AddQueryString(Path(deviceId) + "changes", new Dictionary<string, string?>
        {
            ["from"] = from.ToString("O"), ["until"] = until.ToString("O"), ["scopeId"] = scopeId?.ToString("D"), ["cursor"] = cursor
        }), token)
        : legacyChanges.GetAsync(deviceId, from, until, scopeId, cursor, token);

    public async Task<IntegritySnapshotChunkView?> GetChunkAsync(Guid deviceId, Guid agentId, Guid scopeId, Guid snapshotId, int chunkIndex, CancellationToken token)
    {
        if (!Remote) return await legacy.GetChunkAsync(deviceId, agentId, scopeId, snapshotId, chunkIndex, token);
        var path = QueryHelpers.AddQueryString(Path(deviceId) + $"scopes/{scopeId:D}/snapshots/{snapshotId:D}/chunks/{chunkIndex}", "agentId", agentId.ToString("D"));
        using var response = await clients.CreateClient("security").GetAsync(path, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid integrity query.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IntegritySnapshotChunkView>(cancellationToken: token)
            ?? throw new InvalidOperationException("Security returned an empty integrity chunk.");
    }

    private async Task<T> ReadAsync<T>(string path, CancellationToken token)
    {
        using var response = await clients.CreateClient("security").GetAsync(path, token);
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid integrity query.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("Security returned an empty integrity response.");
    }
}
