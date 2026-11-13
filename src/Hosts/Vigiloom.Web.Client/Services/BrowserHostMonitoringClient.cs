using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Contracts.Agent.Inventory;

namespace Vigiloom.Web.Client.Services;

internal sealed class BrowserHostMonitoringClient(HttpClient client) : IHostMonitoringClient
{
    public Task<DeviceInventoryView?> GetInventoryAsync(Guid deviceId, CancellationToken token) => ReadAsync<DeviceInventoryView>($"api/v1/devices/{deviceId:D}/inventory", token);
    public Task<InventorySnapshotChunkView?> GetInventoryChunkAsync(Guid deviceId, InventorySource source, Guid snapshotId, int chunkIndex, CancellationToken token) => ReadAsync<InventorySnapshotChunkView>($"api/v1/devices/{deviceId:D}/inventory/{(int)source}/snapshots/{snapshotId:D}/chunks/{chunkIndex}", token);
    public Task<HostMetricPage?> GetMetricsAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, AgentMetricKind? kind, CancellationToken token) => ReadAsync<HostMetricPage>($"api/v1/devices/{deviceId:D}/host-metrics?from={Escape(from)}&until={Escape(until)}{Cursor(cursor)}{(kind is null ? "" : $"&kind={(int)kind}")}", token);
    public Task<HostMetricAggregate?> AggregateMetricsAsync(Guid deviceId, Guid agentId, DateTimeOffset from, DateTimeOffset until, AgentMetricKind kind, string source, string? dimension, int bucketSeconds, CancellationToken token) => ReadAsync<HostMetricAggregate>($"api/v1/devices/{deviceId:D}/host-metrics/aggregate?agentId={agentId:D}&from={Escape(from)}&until={Escape(until)}&kind={(int)kind}&source={Escape(source)}{(dimension is null ? "" : $"&dimension={Escape(dimension)}")}&bucketSeconds={bucketSeconds}", token);
    public Task<HostLogPage?> GetLogsAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, CancellationToken token) => ReadAsync<HostLogPage>($"api/v1/devices/{deviceId:D}/host-logs?from={Escape(from)}&until={Escape(until)}{Cursor(cursor)}", token);
    public Task<DeviceIntegrityView?> GetIntegrityAsync(Guid deviceId, CancellationToken token) => ReadAsync<DeviceIntegrityView>($"api/v1/devices/{deviceId:D}/integrity", token);
    public Task<IntegritySnapshotChunkView?> GetIntegrityChunkAsync(Guid deviceId, Guid scopeId, Guid snapshotId, int chunkIndex, CancellationToken token) => ReadAsync<IntegritySnapshotChunkView>($"api/v1/devices/{deviceId:D}/integrity/scopes/{scopeId:D}/snapshots/{snapshotId:D}/chunks/{chunkIndex}", token);
    public Task<IntegrityChangePage?> GetIntegrityChangesAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, CancellationToken token) => ReadAsync<IntegrityChangePage>($"api/v1/devices/{deviceId:D}/integrity/changes?from={Escape(from)}&until={Escape(until)}{Cursor(cursor)}", token);

    private async Task<T?> ReadAsync<T>(string url, CancellationToken token)
    {
        using var response = await client.GetAsync(url, token);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new UnauthorizedAccessException();
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token) ?? throw new HttpRequestException("Empty host-monitoring response.");
    }
    private static string Escape(string? value) => Uri.EscapeDataString(value ?? "");
    private static string Escape(DateTimeOffset value) => Escape(value.ToString("O"));
    private static string Cursor(string? value) => value is null ? "" : $"&cursor={Escape(value)}";
}
