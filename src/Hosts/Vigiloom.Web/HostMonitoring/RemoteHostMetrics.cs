using System.Net;
using System.Net.Http.Json;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Web.HostMonitoring;

internal sealed class RemoteHostMetrics(HttpClient client) : IHostMetricHistory
{
    public Task<HostMetricPage?> GetAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, AgentMetricKind? kind, CancellationToken token) =>
        ReadAsync<HostMetricPage>($"internal/v1/devices/{deviceId:D}/host-metrics?from={Escape(from)}&until={Escape(until)}{(cursor is null ? "" : $"&cursor={Escape(cursor)}")}{(kind is null ? "" : $"&kind={(int)kind}")}", token);

    public Task<HostMetricSeriesPage?> GetSeriesAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, AgentMetricKind kind, int page, CancellationToken token) =>
        ReadAsync<HostMetricSeriesPage>($"internal/v1/devices/{deviceId:D}/host-metrics/series?from={Escape(from)}&until={Escape(until)}&kind={(int)kind}&page={page}", token);

    public Task<HostMetricAggregate?> AggregateAsync(Guid deviceId, Guid agentId, DateTimeOffset from, DateTimeOffset until,
        AgentMetricKind kind, string source, string? dimension, int bucketSeconds, CancellationToken token) =>
        ReadAsync<HostMetricAggregate>($"internal/v1/devices/{deviceId:D}/host-metrics/aggregate?agentId={agentId:D}&from={Escape(from)}&until={Escape(until)}&kind={(int)kind}&source={Escape(source)}{(dimension is null ? "" : $"&dimension={Escape(dimension)}")}&bucketSeconds={bucketSeconds}", token);

    private async Task<T?> ReadAsync<T>(string path, CancellationToken token) where T : class
    {
        using var response = await client.GetAsync(path, token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.BadRequest) throw new ArgumentException("Invalid host metric query.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: token)
            ?? throw new InvalidOperationException("Host monitoring returned an empty metric response.");
    }

    private static string Escape(DateTimeOffset value) => Escape(value.ToString("O"));
    private static string Escape(string value) => Uri.EscapeDataString(value);
}
