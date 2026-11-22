using NetPace.Core;
using NetPace.Core.Clients.Ookla;
using NetPace.Core.Clients.Testing;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Testing.SpeedTests;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
public sealed class SpeedtestNetProbeTests
{
    [TestMethod]
    public void TrimmedRate_UsesFastestThirtyPercentOfRecentTwoThirds()
    {
        (long Bytes, long Milliseconds)[] samples = [
            (100_000_000, 1000), (100_000_000, 1000), (100_000_000, 1000),
            (1_000_000, 1000), (8_000_000, 2000), (3_000_000, 1000),
            (1_000_000, 1000), (1_000_000, 1000), (1_000_000, 1000)];
        // The three fastest early samples are excluded; two of six later samples remain.
        Assert.AreEqual(88d / 3, SpeedMeasurementMath.CalculateTrimmedRate(samples), 0.0001);
    }

    [TestMethod]
    public void TrimmedRate_RoundsSampleCountsUpAndIgnoresInvalidSamples()
    {
        (long Bytes, long Milliseconds)[] samples = [(1_000_000, 1000), (0, 1000), (4_000_000, 2000)];
        Assert.AreEqual(16d, SpeedMeasurementMath.CalculateTrimmedRate(samples), 0.0001);
        Assert.AreEqual(0d, SpeedMeasurementMath.CalculateTrimmedRate([]));
    }
    public TestContext TestContext { get; set; } = null!;
    private static SpeedtestNetProbe CreateProbe(ISpeedTestService service) => SpeedProbeTestFactory.Create(service, 2000);
    private static SpeedtestNetProbe CreateProbe(IHttpMessageHandlerFactory factory) => new(factory, 2000, 4);
    private static SpeedLease Lease() => new(Guid.CreateVersion7(), Guid.CreateVersion7(), 10, 10);
    private static OoklaServer Server() => new() { Id = 18475, Url = "http://speedtest.example.com:8080/speedtest/upload.php", Location = "Taipei", Sponsor = "Example", Country = "Taiwan", Latitude = 25, Longitude = 121 };
    private static SpeedTestResult Measurement(long bytes, long ms, int success = 1, int failed = 0) =>
        new() { BytesProcessed = bytes, ElapsedMilliseconds = ms, RequestsSucceeded = success, RequestsFailed = failed };

    [TestMethod]
    public async Task MeasureAsync_SelectsFastestSampleFromRecentBatches()
    {
        var server = Server();
        var samples = new Queue<SpeedTestResult>([Measurement(1_000_000, 200), Measurement(4_000_000, 800), Measurement(8_000_000, 1000)]);
        var service = new SpeedTestMock
        {
            GetServersAsyncFunc = _ => Task.FromResult<IServer[]>([server]),
            GetFastestServerByLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 2 }),
            GetServerLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 3 }),
            GetDownloadSpeedAsyncFunc = (_, _, _) => Task.FromResult(samples.Dequeue()),
            GetUploadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Measurement(1_000_000, 2000))
        };
        var result = await CreateProbe(service).MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 100, 60), CancellationToken.None);
        Assert.AreEqual(SpeedTestState.Success, result.State);
        Assert.AreEqual(64d, result.DownloadMbps);
        Assert.AreEqual(4d, result.UploadMbps);
        Assert.IsEmpty(samples);
    }

    [TestMethod]
    public async Task MeasureAsync_SubMillisecondFirstBatchRetriesWithoutInventingSpeed()
    {
        var server = Server();
        var downloads = new Queue<SpeedTestResult>([Measurement(1_000_000, 0), Measurement(2_000_000, 2000)]);
        var service = new SpeedTestMock
        {
            GetServersAsyncFunc = _ => Task.FromResult<IServer[]>([server]),
            GetFastestServerByLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 2 }),
            GetServerLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 3 }),
            GetDownloadSpeedAsyncFunc = (_, _, _) => Task.FromResult(downloads.Dequeue()),
            GetUploadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Measurement(1_000_000, 2000))
        };
        var result = await CreateProbe(service).MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 100, 60), CancellationToken.None);
        Assert.AreEqual(SpeedTestState.Success, result.State);
        Assert.AreEqual(8d, result.DownloadMbps, "The unmeasurably short batch must not inflate the speed.");
        Assert.AreEqual(4d, result.UploadMbps);
        Assert.AreEqual(4_000_000L, result.Bytes, "The short batch still counts toward content traffic.");
        Assert.IsEmpty(downloads);
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task MeasureAsync_MissingLargeDownloadFallsBackAndKeepsTheCeiling()
    {
        var handler = new ControlledPhaseHandler(maximumDownloadSide: 1500);
        var result = await CreateProbe(new ControlledPhaseFactory(handler))
            .MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 200, 60), TestContext.CancellationToken);
        Assert.AreEqual(SpeedTestState.Success, result.State, result.DiagnosticCode);
        Assert.IsNotNull(result.DownloadMbps);
        Assert.IsNotNull(result.UploadMbps);
        var sides = handler.DownloadPaths.Select(DownloadSide).ToArray();
        var firstMissing = Array.FindIndex(sides, side => side > 1500);
        Assert.IsGreaterThanOrEqualTo(0, firstMissing);
        var firstFallback = Array.FindIndex(sides, firstMissing + 1, side => side <= 1500);
        Assert.IsGreaterThan(firstMissing, firstFallback);
        Assert.IsTrue(sides.Skip(firstFallback).All(side => side <= 1500), "A later batch must not retry an unsupported size.");
    }

    private static int DownloadSide(string path)
    {
        var file = Path.GetFileNameWithoutExtension(path);
        return int.Parse(file.AsSpan(6, file.IndexOf('x') - 6));
    }

    [TestMethod]
    public async Task MeasureAsync_EmptyLaterDownloadDoesNotTurnShortSampleIntoSuccess()
    {
        var server = Server();
        var downloads = new Queue<SpeedTestResult>([Measurement(1_000_000, 100), Measurement(0, 2000)]);
        var service = new SpeedTestMock
        {
            GetServersAsyncFunc = _ => Task.FromResult<IServer[]>([server]),
            GetFastestServerByLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 2 }),
            GetServerLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 3 }),
            GetDownloadSpeedAsyncFunc = (_, _, _) => Task.FromResult(downloads.Dequeue()),
            GetUploadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Measurement(1_000_000, 2000))
        };
        var result = await CreateProbe(service).MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 100, 60), CancellationToken.None);
        Assert.AreEqual(SpeedTestState.Partial, result.State);
        Assert.IsNotNull(result.DownloadMbps);
        Assert.IsNotNull(result.UploadMbps);
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task MeasureAsync_SlowUploadProbeCannotCountAsTransferTime()
    {
        var handler = new ControlledPhaseHandler(uploadDelayMs: 100, probeDelayMs: 2200);
        var result = await CreateProbe(new ControlledPhaseFactory(handler))
            .MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 200, 12), TestContext.CancellationToken);
        Assert.AreNotEqual(SpeedTestState.Success, result.State);
        Assert.IsGreaterThan(0, handler.UploadRequests);
        Assert.AreEqual(1, handler.UploadProbes);
        Assert.IsNotNull(result.UploadMbps);
        var completedMbps = handler.UploadBytes * 8d / handler.UploadElapsedSeconds / 1_000_000d;
        Assert.IsGreaterThan(completedMbps * 0.4, result.UploadMbps.Value,
            "The initial empty probe must not make the upload appear near zero.");
    }

    [TestMethod]
    public async Task MeasureAsync_UploadTimeoutRetainsLastCompletedSpeed()
    {
        var server = Server();
        var uploadCalls = 0;
        var service = new SpeedTestMock
        {
            GetServersAsyncFunc = _ => Task.FromResult<IServer[]>([server]),
            GetFastestServerByLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 2 }),
            GetServerLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 3 }),
            GetDownloadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Measurement(1_000_000, 2000)),
            GetUploadSpeedAsyncFunc = async (_, _, token) =>
            {
                if (Interlocked.Increment(ref uploadCalls) == 1) return Measurement(1_000_000, 100);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Measurement(0, 0);
            }
        };
        var result = await CreateProbe(service).MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 100, 9), CancellationToken.None);
        Assert.AreEqual(SpeedTestState.Partial, result.State);
        Assert.AreEqual("phase_timeout", result.DiagnosticCode);
        Assert.AreEqual(80d, result.UploadMbps);
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task MeasureAsync_FirstUploadTimesOutWithoutInventingASpeed()
    {
        var handler = new ControlledPhaseHandler(slowUploads: true);
        var result = await new SpeedtestNetProbe(new ControlledPhaseFactory(handler), 200, 4)
            .MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 200, 12), TestContext.CancellationToken);
        Assert.AreEqual(SpeedTestState.Partial, result.State);
        Assert.AreEqual("measurement_incomplete", result.DiagnosticCode);
        Assert.IsNull(result.UploadMbps);
        Assert.IsGreaterThan(0L, handler.SlowUploadBytes);
        Assert.IsNotNull(result.Bytes);
        Assert.IsGreaterThanOrEqualTo(handler.DownloadBytes + handler.SlowUploadBytes, result.Bytes.Value);
    }

    [TestMethod]
    public async Task MeasureAsync_SlowDownloadLeavesTimeForUpload()
    {
        var server = Server();
        var service = new SpeedTestMock
        {
            GetServersAsyncFunc = _ => Task.FromResult<IServer[]>([server]),
            GetFastestServerByLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 2 }),
            GetServerLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 3 }),
            GetDownloadSpeedAsyncFunc = async (_, _, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Measurement(0, 0); },
            GetUploadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Measurement(1_000_000, 2000))
        };
        var result = await CreateProbe(service).MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 100, 5), CancellationToken.None);
        Assert.AreEqual(SpeedTestState.Partial, result.State);
        Assert.IsNull(result.DownloadMbps);
        Assert.AreEqual(4d, result.UploadMbps);
        Assert.AreEqual("phase_timeout", result.DiagnosticCode);
    }

    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task MeasureAsync_PhaseTimeoutPreservesObservedTransportBytes()
    {
        var result = await CreateProbe(new SlowDownloadFactory())
            .MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 200, 12), TestContext.CancellationToken);
        Assert.AreEqual(SpeedTestState.Partial, result.State);
        Assert.AreEqual("phase_timeout", result.DiagnosticCode);
        Assert.IsNotNull(result.DownloadMbps);
        Assert.IsGreaterThan(0d, result.DownloadMbps.Value);
        Assert.IsNotNull(result.Bytes);
        Assert.IsGreaterThan(0L, result.Bytes.Value);
        Assert.IsNotNull(result.UploadMbps);
    }

    private sealed class SlowDownloadFactory : IHttpMessageHandlerFactory
    {
        public HttpMessageHandler CreateHandler(string name) => new SlowDownloadHandler();
    }

    private sealed class SlowDownloadHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/js/config-sdk") return new(System.Net.HttpStatusCode.ServiceUnavailable) { RequestMessage = request };
            HttpContent content;
            if (path.EndsWith("speedtest-servers-static.php", StringComparison.Ordinal))
                content = new StringContent("<settings><servers><server url=\"http://speedtest.example.com:8080/speedtest/upload.php\" lat=\"25\" lon=\"121\" name=\"Taipei\" country=\"Taiwan\" sponsor=\"Example\" id=\"1\" /></servers></settings>");
            else if (path.EndsWith("latency.txt", StringComparison.Ordinal)) content = new StringContent("test=test");
            else if (request.Method == HttpMethod.Post)
            {
                if (request.Content is not null) await request.Content.CopyToAsync(Stream.Null, token);
                await Task.Delay(30, token);
                content = new StringContent("ok");
            }
            else content = new StreamContent(new SlowStream());
            return new(System.Net.HttpStatusCode.OK) { Content = content, RequestMessage = request };
        }
    }

    private sealed class SlowStream : MemoryStream
    {
        public SlowStream() : base(new byte[1024 * 1024], writable: false) { }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(100, cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 8192)], cancellationToken);
        }
    }

    [TestMethod]
    public async Task MeasureAsync_UsesFastestServerAndConvertsBytesToMbps()
    {
        var server = Server();
        var service = new SpeedTestMock
        {
            GetServersAsyncFunc = _ => Task.FromResult<IServer[]>([server]),
            GetFastestServerByLatencyAsyncFunc = (servers, _, _) => Task.FromResult(new LatencyTestResult { Server = servers[0], LatencyMilliseconds = 2 }),
            GetServerLatencyAsyncFunc = (s, _, _) => Task.FromResult(new LatencyTestResult { Server = s, LatencyMilliseconds = 3 }),
            GetDownloadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Measurement(2_000_000, 2000)),
            GetUploadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Measurement(1_000_000, 2000))
        };
        var result = await CreateProbe(service).MeasureAsync(Lease(), CancellationToken.None);
        Assert.AreEqual(SpeedTestState.Success, result.State);
        Assert.AreEqual(8d, result.DownloadMbps);
        Assert.AreEqual(4d, result.UploadMbps);
        Assert.AreEqual(3d, result.LatencyMilliseconds);
        Assert.AreEqual(3_000_000L, result.Bytes);
        Assert.AreEqual("Example (#18475) · speedtest.example.com", result.Server);
        Assert.AreEqual("Taipei", result.City);
        Assert.AreEqual(25d, result.Latitude);
        Assert.AreEqual(121d, result.Longitude);
    }

    [TestMethod]
    public async Task MeasureAsync_PhaseBudgetIsNormalCompletionWhenSamplesAreLongEnough()
    {
        var server = Server();
        var service = new SpeedTestMock
        {
            GetServersAsyncFunc = _ => Task.FromResult<IServer[]>([server]),
            GetFastestServerByLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 2 }),
            GetServerLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 3 }),
            GetDownloadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Measurement(100 * 1048576, 8000)),
            GetUploadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Measurement(72 * 1048576, 15000))
        };
        var result = await CreateProbe(service).MeasureAsync(new SpeedLease(Guid.CreateVersion7(), Guid.CreateVersion7(), 200, 60), CancellationToken.None);
        Assert.AreEqual(SpeedTestState.Success, result.State);
        Assert.AreEqual(104.8576d, result.DownloadMbps!.Value, 0.000001d);
        Assert.AreEqual(40.2653184d, result.UploadMbps!.Value, 0.000001d);
        Assert.IsNull(result.DiagnosticCode);
    }

    [TestMethod]
    public async Task MeasureAsync_ZeroElapsedTimeDoesNotProduceSpeed()
    {
        var server = Server();
        var service = new SpeedTestMock
        {
            GetServersAsyncFunc = _ => Task.FromResult<IServer[]>([server]),
            GetFastestServerByLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 2 }),
            GetServerLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 3 }),
            GetDownloadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Measurement(1024, 0)),
            GetUploadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Measurement(1024, 0))
        };
        var result = await CreateProbe(service).MeasureAsync(Lease(), CancellationToken.None);
        Assert.AreEqual(SpeedTestState.Failed, result.State);
        Assert.IsNull(result.DownloadMbps);
        Assert.IsNull(result.UploadMbps);
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task MeasureAsync_RealClientCompletesWhenOnlyDownloadPhaseReachesItsBudget()
    {
        var handler = new ControlledPhaseHandler();
        var result = await CreateProbe(new ControlledPhaseFactory(handler)).MeasureAsync(
            new SpeedLease(Guid.CreateVersion7(), Guid.CreateVersion7(), 200, 60), TestContext.CancellationToken);
        Assert.AreEqual(SpeedTestState.Success, result.State);
        Assert.IsNotNull(result.DownloadMbps);
        Assert.IsNotNull(result.UploadMbps);
        Assert.IsNotNull(result.Bytes);
        Assert.IsGreaterThan(0L, result.Bytes.Value);
        Assert.IsLessThan(200L * 1048576, result.Bytes.Value);
        Assert.IsGreaterThan(0, handler.UploadRequests);
        Assert.IsGreaterThan(0L, handler.UploadBytes);
        Assert.IsNull(result.DiagnosticCode);
    }

    [TestMethod]
    [DataRow(91d, 121d)]
    [DataRow(25d, -181d)]
    [DataRow(double.NaN, 0d)]
    [DataRow(0d, double.PositiveInfinity)]
    public async Task MeasureAsync_InvalidCoordinatesAreNotReported(double latitude, double longitude)
    {
        var server = Server(); server.Latitude = latitude; server.Longitude = longitude;
        var service = new SpeedTestMock
        {
            GetServersAsyncFunc = _ => Task.FromResult<IServer[]>([server]),
            GetFastestServerByLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 2 }),
            GetServerLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 3 }),
            GetDownloadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Measurement(2_000_000, 2000)),
            GetUploadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Measurement(1_000_000, 2000))
        };
        var result = await CreateProbe(service).MeasureAsync(Lease(), CancellationToken.None);
        Assert.AreEqual(SpeedTestState.Success, result.State);
        Assert.IsNull(result.Latitude);
        Assert.IsNull(result.Longitude);
    }

    [TestMethod]
    public async Task MeasureAsync_RejectsInvalidServerAndDoesNotMeasure()
    {
        var invalid = Server(); invalid.Url = "http://127.0.0.1/speedtest/upload.php";
        var service = new SpeedTestMock { GetServersAsyncFunc = _ => Task.FromResult<IServer[]>([invalid]) };
        var result = await CreateProbe(service).MeasureAsync(Lease(), CancellationToken.None);
        Assert.AreEqual(SpeedTestState.NoServer, result.State);
        Assert.IsNull(result.DownloadMbps);
    }

    [TestMethod]
    public async Task MeasureAsync_FailedUploadIsNotZeroOrSuccessful()
    {
        var server = Server();
        var service = new SpeedTestMock
        {
            GetServersAsyncFunc = _ => Task.FromResult<IServer[]>([server]),
            GetFastestServerByLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 2 }),
            GetServerLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 3 }),
            GetDownloadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Measurement(1_000_000, 1000)),
            GetUploadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Measurement(0, 1000, success: 0, failed: 1))
        };
        var result = await CreateProbe(service).MeasureAsync(Lease(), CancellationToken.None);
        Assert.AreEqual(SpeedTestState.Partial, result.State);
        Assert.AreEqual(8d, result.DownloadMbps);
        Assert.IsNull(result.UploadMbps);
    }

    [TestMethod]
    public async Task MeasureAsync_AlreadyCancelledDoesNotContactProvider()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        var result = await CreateProbe(new SpeedTestMock()).MeasureAsync(Lease(), source.Token);
        Assert.AreEqual(SpeedTestState.Cancelled, result.State);
        Assert.IsNull(result.Bytes);
    }

    [TestMethod]
    public async Task MeasureAsync_EnforcesSharedBudgetAcrossConcurrentResponseStreams()
    {
        var factory = new FixtureHandlerFactory();
        var probe = CreateProbe(factory);
        var result = await probe.MeasureAsync(new SpeedLease(Guid.CreateVersion7(), Guid.CreateVersion7(), 2, 10), CancellationToken.None);
        Assert.AreEqual(SpeedTestState.BudgetExceeded, result.State);
        Assert.IsNotNull(result.Bytes);
        Assert.IsLessThanOrEqualTo(2L * 1024 * 1024, result.Bytes.Value);
        Assert.IsLessThanOrEqualTo(12, factory.Handler.Requests);
    }

    [TestMethod]
    public async Task MeasureAsync_CancelsInProgressProviderRequest()
    {
        var service = new SpeedTestMock { GetServersAsyncFunc = async token => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return []; } };
        using var source = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var result = await CreateProbe(service).MeasureAsync(Lease(), source.Token);
        Assert.AreEqual(SpeedTestState.Cancelled, result.State);
        Assert.IsNull(result.DownloadMbps);
        Assert.IsNull(result.UploadMbps);
    }

    [TestMethod]
    public async Task MeasureAsync_DeadlineProducesTimeoutWithoutZeroSpeeds()
    {
        var service = new SpeedTestMock { GetServersAsyncFunc = async token => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return []; } };
        var result = await CreateProbe(service).MeasureAsync(new SpeedLease(Guid.CreateVersion7(), Guid.CreateVersion7(), 10, 1), CancellationToken.None);
        Assert.AreEqual(SpeedTestState.TimedOut, result.State);
        Assert.AreEqual("timeout", result.DiagnosticCode);
        Assert.IsNull(result.DownloadMbps);
        Assert.IsNull(result.UploadMbps);
    }

    [TestMethod]
    public async Task MeasureAsync_ProviderFailureDoesNotProduceZeroSpeeds()
    {
        var service = new SpeedTestMock { GetServersAsyncFunc = _ => Task.FromException<IServer[]>(new HttpRequestException()) };
        var result = await CreateProbe(service).MeasureAsync(Lease(), CancellationToken.None);
        Assert.AreEqual(SpeedTestState.Failed, result.State);
        Assert.AreEqual("provider_error", result.DiagnosticCode);
        Assert.IsNull(result.DownloadMbps);
        Assert.IsNull(result.UploadMbps);
    }

    [TestMethod]
    public async Task MeasureAsync_CancelDuringUploadPreservesCompletedDownload()
    {
        var server = Server();
        var uploadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new SpeedTestMock
        {
            GetServersAsyncFunc = _ => Task.FromResult<IServer[]>([server]),
            GetFastestServerByLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 2 }),
            GetServerLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 3 }),
            GetDownloadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Measurement(2_000_000, 2000)),
            GetUploadSpeedAsyncFunc = async (_, _, token) => { uploadStarted.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Measurement(0, 0); }
        };
        using var source = new CancellationTokenSource();
        var pending = CreateProbe(service).MeasureAsync(Lease(), source.Token);
        await uploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        await source.CancelAsync();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CancellationToken);
        Assert.AreEqual(SpeedTestState.Cancelled, result.State);
        Assert.AreEqual(8d, result.DownloadMbps);
        Assert.IsNull(result.UploadMbps);
        Assert.AreEqual("cancelled", result.DiagnosticCode);
    }

    [TestMethod]
    public async Task MeasureAsync_StopsParallelUploadsAtPhaseBudget()
    {
        var factory = new FixtureHandlerFactory(smallDownloads: true);
        var result = await CreateProbe(factory).MeasureAsync(new SpeedLease(Guid.CreateVersion7(), Guid.CreateVersion7(), 2, 10), CancellationToken.None);
        Assert.AreEqual(SpeedTestState.BudgetExceeded, result.State);
        Assert.IsGreaterThan(0, factory.Handler.UploadRequests);
        Assert.IsNotNull(result.Bytes);
        Assert.IsLessThanOrEqualTo(2L * 1024 * 1024, result.Bytes.Value);
    }

    private sealed class FixtureHandlerFactory(bool smallDownloads = false) : IHttpMessageHandlerFactory
    {
        public FixtureHandler Handler { get; } = new(smallDownloads);
        public HttpMessageHandler CreateHandler(string name) => Handler;
    }

    private sealed class FixtureHandler(bool smallDownloads) : HttpMessageHandler
    {
        private int requests;
        private int uploadRequests;
        public int Requests => requests;
        public int UploadRequests => uploadRequests;
        private static readonly byte[] Payload = new byte[4 * 1024 * 1024];
        private static readonly byte[] SmallPayload = new byte[8192];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requests);
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/js/config-sdk") return new(System.Net.HttpStatusCode.ServiceUnavailable) { RequestMessage = request };
            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref uploadRequests);
                if (request.Content is not null) await request.Content.CopyToAsync(Stream.Null, cancellationToken);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("ok"), RequestMessage = request };
            }
            if (smallDownloads && !path.EndsWith("speedtest-servers-static.php", StringComparison.Ordinal) && !path.EndsWith("latency.txt", StringComparison.Ordinal))
                await Task.Delay(5, cancellationToken);
            HttpContent content = path.EndsWith("speedtest-servers-static.php", StringComparison.Ordinal)
                ? new StringContent("<settings><servers><server url=\"http://speedtest.example.com:8080/speedtest/upload.php\" lat=\"25\" lon=\"121\" name=\"Taipei\" country=\"Taiwan\" sponsor=\"Example\" id=\"1\" /></servers></settings>")
                : path.EndsWith("latency.txt", StringComparison.Ordinal) ? new StringContent("test=test") : new ByteArrayContent(smallDownloads ? SmallPayload : Payload);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content, RequestMessage = request };
        }
    }
}
