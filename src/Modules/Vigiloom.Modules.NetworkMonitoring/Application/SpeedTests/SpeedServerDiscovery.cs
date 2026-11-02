using System.Globalization;
using System.Net;
using System.Text.Json;
using NetPace.Core;
using NetPace.Core.Clients.Ookla;

namespace Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

internal static class SpeedServerDiscovery
{
    internal static async Task<IServer[]> RankServersAsync(ISpeedTestService service, IEnumerable<IServer> candidates,
        CancellationToken token)
    {
        using var screeningDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        screeningDeadline.CancelAfter(TimeSpan.FromSeconds(5));
        var tested = candidates.OfType<OoklaServer>().DistinctBy(server => server.Id).Take(10).ToArray();
        var results = await Task.WhenAll(tested.Select(async server =>
        {
            var successes = 0;
            try
            {
                var diagnostics = new InlineProgress<RequestDiagnostic>(request =>
                {
                    if (request.Outcome == RequestOutcome.Succeeded) Interlocked.Increment(ref successes);
                });
                var result = await service.GetServerLatencyAsync(server, null, diagnostics, screeningDeadline.Token);
                return (Server: server, Latency: Volatile.Read(ref successes) > 0 && double.IsFinite(result.LatencyMilliseconds) && result.LatencyMilliseconds >= 0
                    ? (double?)result.LatencyMilliseconds : null);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return (Server: server, Latency: (double?)null); }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or ArgumentException)
            {
                return (Server: server, Latency: (double?)null);
            }
        }));
        token.ThrowIfCancellationRequested();
        return results.Where(result => result.Latency.HasValue).OrderBy(result => result.Latency).Select(result => (IServer)result.Server).ToArray();
    }

    // This endpoint is not documented as a stable public API. Never use its clientAuth
    // token or infer that its server isp_id has the same semantics as the client ispId.
    internal static async Task<IServer[]> GetSuggestedServersAsync(HttpClient transport, CancellationToken token, int? serverId = null)
    {
        using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        requestDeadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var url = serverId is null ? "https://www.speedtest.net/api/js/config-sdk" :
                $"https://www.speedtest.net/api/js/config-sdk?server_ids%5B%5D={serverId.Value.ToString(CultureInfo.InvariantCulture)}";
            using var response = await transport.GetAsync(url, requestDeadline.Token);
            response.EnsureSuccessStatusCode();
            using var content = await response.Content.ReadAsStreamAsync(requestDeadline.Token);
            using var json = await JsonDocument.ParseAsync(content, cancellationToken: requestDeadline.Token);
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("servers", out var servers) || servers.ValueKind != JsonValueKind.Array) return [];
            var candidates = new List<IServer>();
            foreach (var item in servers.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string? Value(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (!int.TryParse(Value("id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 ||
                    !double.TryParse(Value("lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) ||
                    !double.TryParse(Value("lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon)) continue;
                var server = new OoklaServer { Id = id, Url = Value("url") ?? "", Location = Value("name") ?? "",
                    Sponsor = Value("sponsor") ?? "", Country = Value("country") ?? "", Latitude = lat, Longitude = lon };
                if (IsValidCandidate(server)) candidates.Add(server);
                if (candidates.Count == 20) break;
            }
            return [.. candidates];
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return []; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException) { return []; }
    }

    internal static bool IsValidCandidate(IServer server) => Uri.TryCreate(server.Url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.Port is 80 or 443 or 8080 && string.IsNullOrEmpty(uri.UserInfo)
        && !IPAddress.TryParse(uri.Host, out _) && uri.AbsolutePath.StartsWith("/speedtest/", StringComparison.OrdinalIgnoreCase)
        && server.Location.Length <= 100 && server.Sponsor.Length <= 100 && (server is not OoklaServer ookla || ookla.Country is null || ookla.Country.Length <= 100);

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

}
