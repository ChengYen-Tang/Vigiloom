using System.Net;
using System.Net.Sockets;
using NetPace.Core;

namespace Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

internal static class SpeedHttpTransport
{
    internal static SocketsHttpHandler CreatePrimaryHandler() => new()
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5), ConnectCallback = ConnectPublicAsync,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    internal static HttpClient CreateTransport(IHttpMessageHandlerFactory factory, SpeedTrafficBudget budget, out EndpointGuard guard)
    {
        guard = new EndpointGuard(budget) { InnerHandler = factory.CreateHandler(SpeedtestNetProbe.HttpClientName) };
        var client = new HttpClient(guard) { MaxResponseContentBufferSize = 2 * 1024 * 1024, Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Vigiloom/1.0");
        return client;
    }

    internal sealed class EndpointGuard(SpeedTrafficBudget budget) : DelegatingHandler
    {
        private readonly HashSet<string> hosts = new(StringComparer.OrdinalIgnoreCase) { "c.speedtest.net", "www.speedtest.net" };
        private DownloadBatchResponses? downloadBatch;
        public DownloadBatchResponses TrackDownloadBatch()
        {
            var batch = new DownloadBatchResponses();
            Volatile.Write(ref downloadBatch, batch);
            return batch;
        }
        public void StopTrackingDownloadBatch(DownloadBatchResponses batch) => Interlocked.CompareExchange(ref downloadBatch, null, batch);
        public void Allow(IEnumerable<IServer> servers)
        {
            foreach (var server in servers) hosts.Add(new Uri(server.Url).Host);
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri;
            if (budget.Reached) throw new HttpRequestException("Speed-test traffic budget exhausted.");
            if (uri is null || !hosts.Contains(uri.Host) || uri.Port is not (80 or 443 or 8080) ||
                uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
                throw new HttpRequestException("Untrusted speed test endpoint.");
            if (request.Content is not null) request.Content = new SpeedBudgetContent(request.Content, budget, TrafficDirection.Sent);
            var batch = request.Method == HttpMethod.Get && uri.AbsolutePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                ? Volatile.Read(ref downloadBatch) : null;
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                batch?.Record(response.StatusCode);
                if (response.Content is not null) response.Content = new SpeedBudgetContent(response.Content, budget, TrafficDirection.Received);
                return response;
            }
            catch
            {
                batch?.RecordOther();
                throw;
            }
        }
    }

    internal sealed class DownloadBatchResponses
    {
        private int missing, other;
        public void Record(HttpStatusCode status)
        {
            if (status is HttpStatusCode.NotFound or HttpStatusCode.Gone) Interlocked.Increment(ref missing);
            else Interlocked.Increment(ref other);
        }
        public void RecordOther() => Interlocked.Increment(ref other);
        public bool OnlyMissingFiles(int failures) => failures > 0 && Volatile.Read(ref missing) == failures && Volatile.Read(ref other) == 0;
    }

    private static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        if (context.DnsEndPoint.Port is not (80 or 443 or 8080)) throw new HttpRequestException("Unsupported speed test port.");
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
        foreach (var address in addresses.Where(IsPublic))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token); return new NetworkStream(socket, ownsSocket: true); }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("No public speed test address is reachable.");
    }

    private static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        if (b.Length == 16) return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8);
        return b.Length == 4 && b[0] is > 0 and < 224 && b[0] != 10 && b[0] != 127 &&
            !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
            !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] is >= 64 and <= 127);
    }
}
