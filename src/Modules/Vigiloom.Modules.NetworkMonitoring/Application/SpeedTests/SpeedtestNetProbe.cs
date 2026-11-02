using System.Net;
using System.Net.Sockets;
using System.Globalization;
using System.Text.Json;
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

/// <summary>Uses NetPace for discovery and download, and continuous HTTP upload lanes.</summary>
public sealed class SpeedtestNetProbe : ISpeedProbe
{
    public const string HttpClientName = "SpeedtestNet";
    internal const int DefaultTargetMilliseconds = 15_000;
    internal const int DefaultParallelRequests = 4;
    private readonly ISpeedTestService? testService;
    private readonly Func<IServer, SpeedLease, Action<double?>, CancellationToken,
        Task<(long Bytes, long Milliseconds, int Failures, bool TimedOut)>>? testUpload;
    private readonly IHttpMessageHandlerFactory? handlerFactory;
    private readonly int targetMilliseconds;
    private readonly int parallelRequests;
    private readonly bool defaultMultiServer;

    public SpeedtestNetProbe(IHttpMessageHandlerFactory handlerFactory) : this(handlerFactory, DefaultTargetMilliseconds, DefaultParallelRequests, true) { }
    internal SpeedtestNetProbe(IHttpMessageHandlerFactory handlerFactory, int targetMilliseconds, int parallelRequests, bool defaultMultiServer = false)
    {
        this.handlerFactory = handlerFactory;
        this.targetMilliseconds = targetMilliseconds;
        this.parallelRequests = parallelRequests;
        this.defaultMultiServer = defaultMultiServer;
    }
    internal SpeedtestNetProbe(ISpeedTestService testService, int targetMilliseconds,
        Func<IServer, SpeedLease, Action<double?>, CancellationToken,
            Task<(long Bytes, long Milliseconds, int Failures, bool TimedOut)>> testUpload)
    {
        this.testService = testService;
        this.testUpload = testUpload;
        this.targetMilliseconds = targetMilliseconds;
        parallelRequests = DefaultParallelRequests;
    }

    public Task<SpeedMeasurement> MeasureAsync(SpeedLease lease, CancellationToken token) =>
        MeasureAsync(lease, null, token, multiServerDownload: defaultMultiServer);

    public async Task<SpeedMeasurement> MeasureAsync(SpeedLease lease, int? serverId, CancellationToken token,
        bool multiServerDownload = false, IReadOnlyList<int>? fixedDownloadServerIds = null)
    {
        if (serverId is <= 0) throw new ArgumentOutOfRangeException(nameof(serverId));
        if (fixedDownloadServerIds is not null && (serverId is not null || !multiServerDownload ||
            fixedDownloadServerIds.Count is < 1 or > 4 || fixedDownloadServerIds.Any(id => id <= 0) ||
            fixedDownloadServerIds.Distinct().Count() != fixedDownloadServerIds.Count))
            throw new ArgumentException("Specify one to four distinct positive server IDs in multi-server mode.", nameof(fixedDownloadServerIds));
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
        var service = testService ?? CreateService(1, transport!, parallelRequests);
        IServer? selected = null;
        IServer[] downloadServers = [];
        double? latency = null, download = null, upload = null;
        long? bytes = null;
        try
        {
            var candidates = fixedDownloadServerIds is null
                ? transport is null ? Array.Empty<IServer>() : await GetSuggestedServersAsync(transport, deadline.Token, serverId)
                : await ResolveFixedServersAsync(fixedDownloadServerIds);
            if (serverId is not null) candidates = candidates.Where(MatchesServer).ToArray();
            var usedSuggestions = candidates.Length > 0;
            if (fixedDownloadServerIds is not null && candidates.Length != fixedDownloadServerIds.Count)
                return new(SpeedTestState.NoServer, Bytes: transport is null ? null : budget.BytesTransferred, DiagnosticCode: "fixed_server_not_found");
            if (candidates.Length == 0 && fixedDownloadServerIds is null)
                candidates = (await service.GetServersAsync(deadline.Token)).Where(IsValidCandidate).Where(MatchesServer)
                    .Take(serverId is null ? 20 : 1).ToArray();
            if (candidates.Length == 0) return new(SpeedTestState.NoServer, DiagnosticCode: serverId is null ? "no_valid_servers" : "server_not_found");
            guard?.Allow(candidates);
            if (multiServerDownload && serverId is null && transport is not null)
            {
                var ranked = await RankServersAsync(service, candidates, deadline.Token);
                if (ranked.Length == 0 && usedSuggestions && !budget.Reached && fixedDownloadServerIds is null)
                {
                    candidates = (await service.GetServersAsync(deadline.Token)).Where(IsValidCandidate).Take(20).ToArray();
                    guard!.Allow(candidates);
                    ranked = await RankServersAsync(service, candidates, deadline.Token);
                }
                if (ranked.Length == 0)
                    return new(budget.Reached ? SpeedTestState.BudgetExceeded : SpeedTestState.NoServer,
                        Bytes: budget.BytesTransferred, DiagnosticCode: budget.Reached ? "traffic_budget" : "no_reachable_servers");
                if (fixedDownloadServerIds is not null && ranked.Length != fixedDownloadServerIds.Count)
                    return new(SpeedTestState.NoServer, Bytes: budget.BytesTransferred, DiagnosticCode: "fixed_server_unreachable");
                downloadServers = fixedDownloadServerIds is null ? ranked.Take(parallelRequests).ToArray()
                    : fixedDownloadServerIds.Select(id => ranked.Single(candidate => candidate is OoklaServer ookla && ookla.Id == id)).ToArray();
                selected = downloadServers[0];
            }
            else
            {
                LatencyTestResult fastest;
                try { fastest = await service.GetFastestServerByLatencyAsync(candidates, deadline.Token); }
                catch (Exception ex) when (ex.Message == "No servers available")
                {
                    if (!usedSuggestions || budget.Reached || serverId is not null)
                        return new(budget.Reached ? SpeedTestState.BudgetExceeded : SpeedTestState.NoServer,
                            Bytes: transport is null ? null : budget.BytesTransferred, DiagnosticCode: budget.Reached ? "traffic_budget" : "no_reachable_servers");
                    candidates = (await service.GetServersAsync(deadline.Token)).Where(IsValidCandidate).Take(20).ToArray();
                    if (candidates.Length == 0) return new(SpeedTestState.NoServer, Bytes: budget.BytesTransferred, DiagnosticCode: "no_reachable_servers");
                    guard!.Allow(candidates);
                    try { fastest = await service.GetFastestServerByLatencyAsync(candidates, deadline.Token); }
                    catch (Exception fallbackError) when (fallbackError.Message == "No servers available")
                    {
                        return new(budget.Reached ? SpeedTestState.BudgetExceeded : SpeedTestState.NoServer,
                            Bytes: budget.BytesTransferred, DiagnosticCode: budget.Reached ? "traffic_budget" : "no_reachable_servers");
                    }
                }
                selected = fastest.Server;
                downloadServers = [selected];
            }
            latency = (await service.GetServerLatencyAsync(selected, deadline.Token)).LatencyMilliseconds;
            var down = await MeasureDownloadAsync(value => download = value);
            var up = await MeasureUploadAsync(value => upload = value);
            bytes = down.Bytes + up.Bytes;
            var sufficientDuration = down.Milliseconds >= targetMilliseconds && up.Milliseconds >= targetMilliseconds;
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

        bool MatchesServer(IServer candidate) => serverId is null || candidate is OoklaServer server && server.Id == serverId;

        async Task<IServer[]> ResolveFixedServersAsync(IReadOnlyList<int> ids)
        {
            var found = new Dictionary<int, IServer>();
            if (transport is not null)
            {
                var responses = await Task.WhenAll(ids.Select(id => GetSuggestedServersAsync(transport, deadline.Token, id)));
                foreach (var candidate in responses.SelectMany(response => response).OfType<OoklaServer>())
                    if (ids.Contains(candidate.Id)) found.TryAdd(candidate.Id, candidate);
            }
            if (found.Count != ids.Count)
            {
                var directory = await service.GetServersAsync(deadline.Token);
                foreach (var candidate in directory.OfType<OoklaServer>().Where(IsValidCandidate))
                    if (ids.Contains(candidate.Id)) found.TryAdd(candidate.Id, candidate);
            }
            return ids.Where(found.ContainsKey).Select(id => found[id]).ToArray();
        }

        Task<(long Bytes, long Milliseconds, int Failures, bool TimedOut)> MeasureUploadAsync(Action<double?> update) =>
            testUpload is not null
                ? testUpload(selected!, lease, update, deadline.Token)
                : ContinuousSpeedUpload.RunAsync(transport!, selected!, budget, lease.MaxMiB,
                    targetMilliseconds, PhaseDeadlineSeconds(lease.TimeoutSeconds),
                    multiServerDownload && serverId is null ? 6 : parallelRequests,
                    update, deadline.Token);

        async Task<(long Bytes, long Milliseconds, int Failures, bool TimedOut)> MeasureDownloadAsync(Action<double?> update)
        {
            long total = 0, milliseconds = 0;
            var rateSamples = new List<(long Bytes, long Milliseconds)>();
            var failures = 0;
            var timedOut = false;
            var batchMiB = 1;
            var maximumDownloadSize = 4000;
            var downloadConcurrency = parallelRequests;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            using var phaseDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            phaseDeadline.CancelAfter(TimeSpan.FromSeconds(PhaseDeadlineSeconds(lease.TimeoutSeconds)));
            var phaseLimit = Math.Max(1, lease.MaxMiB / 2) * 1024L * 1024L;
            for (var round = 0; round < 256 && milliseconds < targetMilliseconds && total < phaseLimit; round++)
            {
                var downloadSize = DownloadSize(batchMiB, maximumDownloadSize);
                var current = testService ?? CreateService(batchMiB, transport!, parallelRequests, maximumDownloadSize);
                var before = budget.BytesReceived;
                var batchClock = System.Diagnostics.Stopwatch.StartNew();
                var responses = guard?.TrackDownloadBatch();
                SpeedTestResult sample;
                try
                {
                    sample = multiServerDownload && serverId is null && transport is not null
                        ? await MeasureMultipleAsync()
                        : await current.GetDownloadSpeedAsync(selected!, phaseDeadline.Token);

                    async Task<SpeedTestResult> MeasureMultipleAsync()
                    {
                        // Distribute a shared connection limit across all servers and measure the
                        // combined transfer over one common wall-clock interval.
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        var perServerMiB = Math.Max(1, batchMiB / downloadServers.Length);
                        var targetBytes = Math.Min((long)batchMiB * 1024 * 1024, Math.Max(0, phaseLimit - total));
                        using var batchDeadline = CancellationTokenSource.CreateLinkedTokenSource(phaseDeadline.Token);
                        var tasks = downloadServers.Select(async (server, index) =>
                        {
                            var perServerConnections = downloadConcurrency / downloadServers.Length +
                                (index < downloadConcurrency % downloadServers.Length ? 1 : 0);
                            var worker = CreateService(perServerMiB, transport!, perServerConnections, maximumDownloadSize, downloadSize);
                            long bytesProcessed = 0;
                            var succeeded = 0;
                            var failed = 0;
                            for (var subBatch = 1; subBatch <= 256 && !batchDeadline.IsCancellationRequested; subBatch++)
                            {
                                SpeedTestResult part;
                                try
                                {
                                    part = await worker.GetDownloadSpeedAsync(server, batchDeadline.Token);
                                }
                                catch (OperationCanceledException) when (batchDeadline.IsCancellationRequested && !phaseDeadline.IsCancellationRequested)
                                {
                                    break;
                                }
                                bytesProcessed += Math.Max(0, part.BytesProcessed);
                                succeeded += part.RequestsSucceeded;
                                failed += part.RequestsFailed;
                                if (part.RequestsFailed > 0) break;
                                if (budget.BytesReceived - before >= targetBytes)
                                {
                                    batchDeadline.Cancel();
                                    break;
                                }
                                if (part.RequestsSucceeded == 0)
                                {
                                    if (!batchDeadline.IsCancellationRequested) failed++;
                                    break;
                                }
                            }
                            return (Bytes: bytesProcessed, Succeeded: succeeded, Failed: failed);
                        });
                        var results = await Task.WhenAll(tasks);
                        return new SpeedTestResult { BytesProcessed = results.Sum(result => result.Bytes),
                            ElapsedMilliseconds = clock.ElapsedMilliseconds,
                            RequestsSucceeded = results.Sum(result => result.Succeeded),
                            RequestsFailed = results.Sum(result => result.Failed) };
                    }
                }
                catch (OperationCanceledException) when (phaseDeadline.IsCancellationRequested && !deadline.IsCancellationRequested)
                {
                    if (transport is not null)
                    {
                        var partialBytes = Math.Max(0, budget.BytesReceived - before);
                        total += partialBytes;
                        if (partialBytes > 0)
                        {
                            milliseconds = stopwatch.ElapsedMilliseconds;
                            if (batchClock.ElapsedMilliseconds > 0)
                                rateSamples.Add((partialBytes, batchClock.ElapsedMilliseconds));
                            if (rateSamples.Count > 0) update(CalculateTrimmedRate(rateSamples));
                        }
                    }
                    failures++;
                    timedOut = true;
                    break;
                }
                finally
                {
                    if (responses is not null) guard!.StopTrackingDownloadBatch(responses);
                }
                if (downloadSize > 1000 && sample.RequestsSucceeded == 0 &&
                    responses?.OnlyMissingFiles(sample.RequestsFailed) == true)
                {
                    // Only explicit 404/410 responses prove this particular asset is absent.
                    // Remember the smaller ceiling so a later batch cannot retry the missing size.
                    maximumDownloadSize = SmallerDownloadSize(downloadSize);
                    continue;
                }
                var transferred = transport is null ? sample.BytesProcessed : budget.BytesReceived - before;
                total += Math.Max(0, transferred);
                milliseconds += Math.Max(0, sample.ElapsedMilliseconds);
                failures += sample.RequestsFailed;
                if (sample.RequestsSucceeded == 0 || sample.ElapsedMilliseconds < 0 || transferred <= 0)
                {
                    failures++;
                    if (rateSamples.Count > 0) update(CalculateTrimmedRate(rateSamples));
                    break;
                }
                if (sample.ElapsedMilliseconds == 0)
                {
                    // Count the traffic, but omit an unmeasurably short batch from the rate.
                    // Try a larger batch while a whole MiB of the phase budget remains.
                    var remainingMiB = (phaseLimit - total) / (1024 * 1024);
                    if (remainingMiB < 1) break;
                    batchMiB = (int)Math.Min(batchMiB * 4L, remainingMiB);
                    continue;
                }
                rateSamples.Add((transferred, sample.ElapsedMilliseconds));
                update(CalculateTrimmedRate(rateSamples));
                if (multiServerDownload && serverId is null && transport is not null && milliseconds < targetMilliseconds / 2)
                    downloadConcurrency = NextDownloadConcurrency(downloadConcurrency, downloadServers.Length, transferred, sample.ElapsedMilliseconds);
                batchMiB = NextBatchMiB(batchMiB, transferred, sample.ElapsedMilliseconds, milliseconds, phaseLimit - total, targetMilliseconds);
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

    // Use batches as samples: discard the first third in chronological order,
    // then retain the fastest 30% of the remaining batches (rounding up).
    // Average retained bytes over retained transfer time rather than averaging rates.
    internal static double CalculateTrimmedRate(IReadOnlyList<(long Bytes, long Milliseconds)> samples)
    {
        var valid = samples.Where(sample => sample.Bytes > 0 && sample.Milliseconds > 0).ToArray();
        if (valid.Length == 0) return 0;
        var recent = valid.Skip(valid.Length / 3).ToArray();
        var count = (int)Math.Ceiling(recent.Length * 0.3d);
        var selected = recent.OrderByDescending(sample => (double)sample.Bytes / sample.Milliseconds).Take(count).ToArray();
        return selected.Sum(sample => (double)sample.Bytes) * 8d / selected.Sum(sample => (double)sample.Milliseconds) / 1000d;
    }

    internal static int NextBatchMiB(int previousMiB, long sampleBytes, long sampleMs, long elapsedMs, long remainingBytes, int targetMilliseconds = DefaultTargetMilliseconds)
    {
        var remainingMiB = Math.Max(0, remainingBytes / (1024d * 1024d));
        // Limit the planned sample to roughly three seconds so a throughput drop
        // cannot leave a single oversized batch running until the phase deadline.
        var remainingTargetMs = Math.Min(3000d, Math.Max(0, targetMilliseconds - elapsedMs));
        var estimateMiB = sampleBytes / (1024d * 1024d) * remainingTargetMs / Math.Max(1, sampleMs);
        return (int)Math.Clamp(Math.Ceiling(estimateMiB), 1, Math.Max(1, Math.Min(remainingMiB, previousMiB * 8d)));
    }

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

    internal static double PhaseDeadlineSeconds(int timeoutSeconds) => Math.Max(3, timeoutSeconds / 2d - 2);

    internal static int NextDownloadConcurrency(int current, int serverCount, long bytes, long milliseconds)
    {
        if (serverCount < 1 || milliseconds <= 0 || bytes <= 0) return current;
        var max = Math.Min(24, serverCount * 6);
        var estimated = bytes / (double)milliseconds * 1000d / 750_000d;
        return Math.Clamp((int)Math.Min(max, Math.Ceiling(estimated)), Math.Min(current, max), max);
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

    private static bool IsValidCandidate(IServer server) => Uri.TryCreate(server.Url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && uri.Port is 80 or 443 or 8080 && string.IsNullOrEmpty(uri.UserInfo)
        && !IPAddress.TryParse(uri.Host, out _) && uri.AbsolutePath.StartsWith("/speedtest/", StringComparison.OrdinalIgnoreCase)
        && server.Location.Length <= 100 && server.Sponsor.Length <= 100 && (server is not OoklaServer ookla || ookla.Country is null || ookla.Country.Length <= 100);

    private static int DownloadSize(int batchMiB, int maximum) => Math.Min(maximum,
        batchMiB switch { >= 64 => 4000, >= 16 => 3000, >= 8 => 2000, >= 4 => 1500, _ => 1000 });

    private static int SmallerDownloadSize(int size) => size switch
    {
        4000 => 3000, 3000 => 2000, 2000 => 1500, _ => 1000
    };

    private static ISpeedTestService CreateService(int batchMiB, HttpClient transport, int parallelRequests, int maximumDownloadSize = 4000, int? forcedDownloadSize = null)
    {
        var downloadSize = forcedDownloadSize ?? DownloadSize(batchMiB, maximumDownloadSize);
        var settings = new OoklaSpeedtestSettings(Profile.Medium)
        {
            ServerDiscovery = new ServerDiscoverySettings { ServersUrl = "https://c.speedtest.net/speedtest-servers-static.php" },
            LatencyTest = new LatencyTestSettings { LatencyTestIterations = 3, LatencyTestIntervalMilliseconds = 50 },
            DownloadTest = new DownloadTestSettings { DownloadSizeMb = batchMiB, DownloadParallelTasks = parallelRequests, DownloadSizes = [downloadSize], DownloadSizeIterations = Math.Clamp(batchMiB, 8, 128) }
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

    private sealed class DownloadBatchResponses
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

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
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
