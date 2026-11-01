using NetPace.Core;
using NetPace.Core.Clients.Ookla;
using NetPace.Core.Clients.Testing;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
public sealed class SpeedtestNetProbeTests
{
    private static SpeedLease Lease() => new(Guid.CreateVersion7(), Guid.CreateVersion7(), 10, 10);
    private static OoklaServer Server() => new() { Id = 18475, Url = "http://speedtest.example.com:8080/speedtest/upload.php", Location = "Taipei", Sponsor = "Example", Country = "Taiwan", Latitude = 25, Longitude = 121 };
    private static SpeedTestResult Measurement(long bytes, long ms, int success = 1, int failed = 0) =>
        new() { BytesProcessed = bytes, ElapsedMilliseconds = ms, RequestsSucceeded = success, RequestsFailed = failed };

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
        var result = await new SpeedtestNetProbe(service).MeasureAsync(Lease(), CancellationToken.None);
        Assert.AreEqual(SpeedTestState.Success, result.State);
        Assert.AreEqual(8d, result.DownloadMbps);
        Assert.AreEqual(4d, result.UploadMbps);
        Assert.AreEqual(3d, result.LatencyMilliseconds);
        Assert.AreEqual(3_000_000L, result.Bytes);
        Assert.AreEqual("Example (#18475) · speedtest.example.com", result.Server);
        Assert.AreEqual("Taipei", result.City);
    }

    [TestMethod]
    public async Task MeasureAsync_RejectsInvalidServerAndDoesNotMeasure()
    {
        var invalid = Server(); invalid.Url = "http://127.0.0.1/speedtest/upload.php";
        var service = new SpeedTestMock { GetServersAsyncFunc = _ => Task.FromResult<IServer[]>([invalid]) };
        var result = await new SpeedtestNetProbe(service).MeasureAsync(Lease(), CancellationToken.None);
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
        var result = await new SpeedtestNetProbe(service).MeasureAsync(Lease(), CancellationToken.None);
        Assert.AreEqual(SpeedTestState.Partial, result.State);
        Assert.AreEqual(8d, result.DownloadMbps);
        Assert.IsNull(result.UploadMbps);
    }

    [TestMethod]
    public async Task MeasureAsync_AlreadyCancelledDoesNotContactProvider()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        var result = await new SpeedtestNetProbe(new SpeedTestMock()).MeasureAsync(Lease(), source.Token);
        Assert.AreEqual(SpeedTestState.Cancelled, result.State);
        Assert.IsNull(result.Bytes);
    }

    [TestMethod]
    public async Task MeasureAsync_EnforcesSharedBudgetAcrossConcurrentResponseStreams()
    {
        var factory = new FixtureHandlerFactory();
        var probe = new SpeedtestNetProbe(factory);
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
        var result = await new SpeedtestNetProbe(service).MeasureAsync(Lease(), source.Token);
        Assert.AreEqual(SpeedTestState.Cancelled, result.State);
    }

    [TestMethod]
    public async Task MeasureAsync_StopsParallelUploadsAtSharedBudget()
    {
        var factory = new FixtureHandlerFactory(smallDownloads: true);
        var result = await new SpeedtestNetProbe(factory).MeasureAsync(new SpeedLease(Guid.CreateVersion7(), Guid.CreateVersion7(), 2, 10), CancellationToken.None);
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
            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref uploadRequests);
                if (request.Content is not null) await request.Content.CopyToAsync(Stream.Null, cancellationToken);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("ok"), RequestMessage = request };
            }
            HttpContent content = path.EndsWith("speedtest-servers-static.php", StringComparison.Ordinal)
                ? new StringContent("<settings><servers><server url=\"http://speedtest.example.com:8080/speedtest/upload.php\" lat=\"25\" lon=\"121\" name=\"Taipei\" country=\"Taiwan\" sponsor=\"Example\" id=\"1\" /></servers></settings>")
                : path.EndsWith("latency.txt", StringComparison.Ordinal) ? new StringContent("test=test") : new ByteArrayContent(smallDownloads ? SmallPayload : Payload);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content, RequestMessage = request };
        }
    }
}
