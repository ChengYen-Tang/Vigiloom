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
        SpeedHttpTransport.EndpointGuard? guard = null;
        using var transport = testService is null ? SpeedHttpTransport.CreateTransport(handlerFactory!, budget, out guard) : null;
        var service = testService ?? CreateService(1, transport!, parallelRequests);
        IServer? selected = null;
        IServer[] downloadServers = [];
        double? latency = null, download = null, upload = null;
        long? bytes = null;
        try
        {
            var candidates = fixedDownloadServerIds is null
                ? transport is null ? Array.Empty<IServer>() : await SpeedServerDiscovery.GetSuggestedServersAsync(transport, deadline.Token, serverId)
                : await ResolveFixedServersAsync(fixedDownloadServerIds);
            if (serverId is not null) candidates = candidates.Where(MatchesServer).ToArray();
            var usedSuggestions = candidates.Length > 0;
            if (fixedDownloadServerIds is not null && candidates.Length != fixedDownloadServerIds.Count)
                return new(SpeedTestState.NoServer, Bytes: transport is null ? null : budget.BytesTransferred, DiagnosticCode: "fixed_server_not_found");
            if (candidates.Length == 0 && fixedDownloadServerIds is null)
                candidates = (await service.GetServersAsync(deadline.Token)).Where(SpeedServerDiscovery.IsValidCandidate).Where(MatchesServer)
                    .Take(serverId is null ? 20 : 1).ToArray();
            if (candidates.Length == 0) return new(SpeedTestState.NoServer, DiagnosticCode: serverId is null ? "no_valid_servers" : "server_not_found");
            guard?.Allow(candidates);
            if (multiServerDownload && serverId is null && transport is not null)
            {
                var ranked = await SpeedServerDiscovery.RankServersAsync(service, candidates, deadline.Token);
                if (ranked.Length == 0 && usedSuggestions && !budget.Reached && fixedDownloadServerIds is null)
                {
                    candidates = (await service.GetServersAsync(deadline.Token)).Where(SpeedServerDiscovery.IsValidCandidate).Take(20).ToArray();
                    guard!.Allow(candidates);
                    ranked = await SpeedServerDiscovery.RankServersAsync(service, candidates, deadline.Token);
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
                    candidates = (await service.GetServersAsync(deadline.Token)).Where(SpeedServerDiscovery.IsValidCandidate).Take(20).ToArray();
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
                var responses = await Task.WhenAll(ids.Select(id => SpeedServerDiscovery.GetSuggestedServersAsync(transport, deadline.Token, id)));
                foreach (var candidate in responses.SelectMany(response => response).OfType<OoklaServer>())
                    if (ids.Contains(candidate.Id)) found.TryAdd(candidate.Id, candidate);
            }
            if (found.Count != ids.Count)
            {
                var directory = await service.GetServersAsync(deadline.Token);
                foreach (var candidate in directory.OfType<OoklaServer>().Where(SpeedServerDiscovery.IsValidCandidate))
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
                            if (rateSamples.Count > 0) update(SpeedMeasurementMath.CalculateTrimmedRate(rateSamples));
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
                    if (rateSamples.Count > 0) update(SpeedMeasurementMath.CalculateTrimmedRate(rateSamples));
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
                update(SpeedMeasurementMath.CalculateTrimmedRate(rateSamples));
                if (multiServerDownload && serverId is null && transport is not null && milliseconds < targetMilliseconds / 2)
                    downloadConcurrency = SpeedMeasurementMath.NextDownloadConcurrency(downloadConcurrency, downloadServers.Length, transferred, sample.ElapsedMilliseconds);
                batchMiB = SpeedMeasurementMath.NextBatchMiB(batchMiB, transferred, sample.ElapsedMilliseconds, milliseconds, phaseLimit - total, targetMilliseconds);
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

    internal static double PhaseDeadlineSeconds(int timeoutSeconds) => Math.Max(3, timeoutSeconds / 2d - 2);

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

    public static SocketsHttpHandler CreatePrimaryHandler() => SpeedHttpTransport.CreatePrimaryHandler();
}
