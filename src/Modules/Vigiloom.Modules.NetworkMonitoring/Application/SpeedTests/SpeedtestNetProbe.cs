using System.Net;
using System.Net.Sockets;
using NetPace.Core;
using NetPace.Core.Clients.Ookla;
using NetPace.Core.Clients.Ookla.Settings;
using Vigiloom.Contracts.SpeedTests;

namespace Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

public sealed record SpeedMeasurement(SpeedTestState State, string? Server = null, string? City = null, string? Country = null,
    double? DownloadMbps = null, double? UploadMbps = null, double? LatencyMilliseconds = null, long? Bytes = null,
    double? Latitude = null, double? Longitude = null, string? DiagnosticCode = null);

public interface ISpeedProbe
{
    Task<SpeedMeasurement> MeasureAsync(SpeedLease lease, CancellationToken token);
}

/// <summary>Uses the Speedtest.net server directory and the NetPace HTTP measurement client.</summary>
public sealed class SpeedtestNetProbe : ISpeedProbe
{
    public const string HttpClientName = "SpeedtestNet";
    private readonly ISpeedTestService? testService;
    private readonly IHttpMessageHandlerFactory? handlerFactory;

    public SpeedtestNetProbe(IHttpMessageHandlerFactory handlerFactory) => this.handlerFactory = handlerFactory;
    public SpeedtestNetProbe(ISpeedTestService testService) => this.testService = testService;

    public async Task<SpeedMeasurement> MeasureAsync(SpeedLease lease, CancellationToken token)
    {
        if (token.IsCancellationRequested) return new(SpeedTestState.Cancelled, DiagnosticCode: "cancelled_before_start");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(lease.TimeoutSeconds));
        var budget = new SpeedTrafficBudget((long)lease.MaxMiB * 1024 * 1024, () =>
        {
            try { deadline.Cancel(); }
            catch (ObjectDisposedException) { }
        }, deadline.Token);
        EndpointGuard? guard = null;
        using var transport = testService is null ? CreateTransport(handlerFactory!, budget, out guard) : null;
        var service = testService ?? CreateService(1, transport!);
        IServer? selected = null;
        double? latency = null, download = null, upload = null;
        long? bytes = null;
        try
        {
            var candidates = (await service.GetServersAsync(deadline.Token))
                .Where(IsValidCandidate).Take(20).ToArray();
            if (candidates.Length == 0) return new(SpeedTestState.NoServer, DiagnosticCode: "no_valid_servers");
            guard?.Allow(candidates);
            LatencyTestResult fastest;
            try { fastest = await service.GetFastestServerByLatencyAsync(candidates, deadline.Token); }
            catch (Exception ex) when (ex.Message == "No servers available")
            {
                return new(budget.Reached ? SpeedTestState.BudgetExceeded : SpeedTestState.NoServer,
                    Bytes: transport is null ? null : budget.BytesTransferred, DiagnosticCode: budget.Reached ? "traffic_budget" : "no_reachable_servers");
            }
            selected = fastest.Server;
            latency = (await service.GetServerLatencyAsync(selected, deadline.Token)).LatencyMilliseconds;
            var down = await MeasureDirectionAsync(true, value => download = value);
            var up = await MeasureDirectionAsync(false, value => upload = value);
            bytes = down.Bytes + up.Bytes;
            var sufficientDuration = down.Milliseconds >= 2000 && up.Milliseconds >= 2000;
            var state = budget.Reached ? SpeedTestState.BudgetExceeded :
                download is not null && upload is not null && down.Failures == 0 && up.Failures == 0 && sufficientDuration
                ? SpeedTestState.Success : download is not null || upload is not null ? SpeedTestState.Partial : SpeedTestState.Failed;
            return Result(state, state == SpeedTestState.Success ? null : down.TimedOut || up.TimedOut ? "phase_timeout" : sufficientDuration ? "measurement_incomplete" : "short_sample");
        }
        catch (OperationCanceledException)
        {
            return Result(token.IsCancellationRequested ? SpeedTestState.Cancelled : budget.Reached ? SpeedTestState.BudgetExceeded : SpeedTestState.TimedOut,
                token.IsCancellationRequested ? "cancelled" : budget.Reached ? "traffic_budget" : "timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Xml.XmlException or FormatException or InvalidOperationException or ArgumentException)
        {
            return Result(budget.Reached ? SpeedTestState.BudgetExceeded : download is not null || upload is not null ? SpeedTestState.Partial : SpeedTestState.Failed,
                budget.Reached ? "traffic_budget" : "provider_error");
        }

        async Task<(long Bytes, long Milliseconds, int Failures, bool TimedOut)> MeasureDirectionAsync(bool receiving, Action<double?> update)
        {
            long total = 0, milliseconds = 0;
            var failures = 0;
            var timedOut = false;
            var batchMiB = 1;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            using var phaseDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            phaseDeadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(3, lease.TimeoutSeconds / 3d)));
            var phaseLimit = Math.Max(1, lease.MaxMiB / 2) * 1024L * 1024L;
            for (var round = 0; round < 16 && milliseconds < 2000 && total < phaseLimit; round++)
            {
                var current = testService ?? CreateService(batchMiB, transport!);
                var before = receiving ? budget.BytesReceived : budget.BytesSent;
                SpeedTestResult sample;
                try
                {
                    sample = receiving ? await current.GetDownloadSpeedAsync(selected!, phaseDeadline.Token) : await current.GetUploadSpeedAsync(selected!, phaseDeadline.Token);
                }
                catch (OperationCanceledException) when (phaseDeadline.IsCancellationRequested && !deadline.IsCancellationRequested)
                {
                    if (transport is not null)
                    {
                        total += Math.Max(0, (receiving ? budget.BytesReceived : budget.BytesSent) - before);
                        milliseconds = stopwatch.ElapsedMilliseconds;
                        if (total > 0 && milliseconds > 0) update(total * 8d / milliseconds / 1000d);
                    }
                    failures++;
                    timedOut = true;
                    break;
                }
                var transferred = transport is null ? sample.BytesProcessed : (receiving ? budget.BytesReceived : budget.BytesSent) - before;
                total += Math.Max(0, transferred);
                milliseconds = transport is null ? milliseconds + Math.Max(0, sample.ElapsedMilliseconds) : stopwatch.ElapsedMilliseconds;
                failures += sample.RequestsFailed;
                if (sample.RequestsSucceeded == 0 || sample.ElapsedMilliseconds <= 0 || transferred <= 0) break;
                update(total * 8d / milliseconds / 1000d);
                batchMiB = NextBatchMiB(batchMiB, transferred, sample.ElapsedMilliseconds, milliseconds, phaseLimit - total);
            }
            return (total, milliseconds, failures, timedOut);
        }

        SpeedMeasurement Result(SpeedTestState state, string? diagnostic)
        {
            var server = selected as OoklaServer;
            var display = selected is null ? null : server is { Id: > 0 }
                ? $"{server.Sponsor} (#{server.Id}) · {new Uri(selected.Url).Host}"
                : new Uri(selected.Url).Host;
            if (display is { Length: > 253 }) display = display[..253];
            var validCoordinates = server is not null && double.IsFinite(server.Latitude) && double.IsFinite(server.Longitude) &&
                server.Latitude is >= -90 and <= 90 && server.Longitude is >= -180 and <= 180;
            return new(state, display, selected?.Location, server?.Country,
                download, upload, latency, transport is not null ? budget.BytesTransferred : bytes,
                validCoordinates ? server!.Latitude : null, validCoordinates ? server!.Longitude : null, diagnostic);
        }
    }

    internal static int NextBatchMiB(int previousMiB, long sampleBytes, long sampleMs, long elapsedMs, long remainingBytes)
    {
        var remainingMiB = Math.Max(0, remainingBytes / (1024d * 1024d));
        var estimateMiB = sampleBytes / (1024d * 1024d) * Math.Max(0, 2000d - elapsedMs) / Math.Max(1, sampleMs);
        return (int)Math.Clamp(Math.Ceiling(estimateMiB), 1, Math.Max(1, Math.Min(remainingMiB, previousMiB * 8d)));
    }

    private static bool IsValidCandidate(IServer server) => Uri.TryCreate(server.Url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.Port is 80 or 443 or 8080 && string.IsNullOrEmpty(uri.UserInfo)
        && !IPAddress.TryParse(uri.Host, out _) && uri.AbsolutePath.StartsWith("/speedtest/", StringComparison.OrdinalIgnoreCase)
        && server.Location.Length <= 100 && server.Sponsor.Length <= 100;

    private static ISpeedTestService CreateService(int batchMiB, HttpClient transport)
    {
        var settings = new OoklaSpeedtestSettings(Profile.Medium)
        {
            ServerDiscovery = new ServerDiscoverySettings { ServersUrl = "https://c.speedtest.net/speedtest-servers-static.php" },
            LatencyTest = new LatencyTestSettings { LatencyTestIterations = 3, LatencyTestIntervalMilliseconds = 50 },
            DownloadTest = new DownloadTestSettings { DownloadSizeMb = batchMiB, DownloadParallelTasks = 4, DownloadSizes = [1000, 1500, 2000, 3000, 4000], DownloadSizeIterations = Math.Clamp(batchMiB, 8, 128) },
            UploadTest = new UploadTestSettings { UploadSizeMb = batchMiB, UploadParallelTasks = 4, UploadSizeIncrementKb = 128, UploadIncrements = 8, UploadSizeIterations = Math.Clamp((int)Math.Ceiling(batchMiB / 4.5), 1, 64) }
        };
        return new OoklaSpeedtest(settings, transport);
    }

    public static SocketsHttpHandler CreatePrimaryHandler() => new()
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(5), ConnectCallback = ConnectPublicAsync,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    private static HttpClient CreateTransport(IHttpMessageHandlerFactory factory, SpeedTrafficBudget budget, out EndpointGuard guard)
    {
        guard = new EndpointGuard(budget) { InnerHandler = factory.CreateHandler(HttpClientName) };
        var client = new HttpClient(guard) { MaxResponseContentBufferSize = 2 * 1024 * 1024, Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Vigiloom/1.0");
        return client;
    }

    private sealed class EndpointGuard(SpeedTrafficBudget budget) : DelegatingHandler
    {
        private readonly HashSet<string> hosts = new(StringComparer.OrdinalIgnoreCase) { "c.speedtest.net" };
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
            var response = await base.SendAsync(request, cancellationToken);
            if (response.Content is not null) response.Content = new SpeedBudgetContent(response.Content, budget, TrafficDirection.Received);
            return response;
        }
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
