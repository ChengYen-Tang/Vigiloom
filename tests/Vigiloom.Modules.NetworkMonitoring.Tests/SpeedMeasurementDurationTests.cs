using NetPace.Core;
using NetPace.Core.Clients.Ookla;
using NetPace.Core.Clients.Testing;
using System.Net;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
public sealed class SpeedMeasurementDurationTests
{
    public TestContext TestContext { get; set; } = null!;
    private static OoklaServer Server() => new() { Id = 1, Url = "http://speedtest.example.com:8080/speedtest/upload.php", Location = "Taipei", Sponsor = "Example", Country = "Taiwan", Latitude = 25, Longitude = 121 };
    private static SpeedTestResult Sample() => new() { BytesProcessed = 1_048_576, ElapsedMilliseconds = 2000, RequestsSucceeded = 1, RequestsFailed = 0 };

    [TestMethod]
    public void AdaptiveBatch_PrefersThreeSecondSamplesAndRespectsRemainingTarget()
    {
        const long mib = 1024 * 1024;
        Assert.AreEqual(300, SpeedMeasurementMath.NextBatchMiB(128, 100 * mib, 1000, 1000, 1000 * mib));
        Assert.AreEqual(50, SpeedMeasurementMath.NextBatchMiB(128, 100 * mib, 1000, 14500, 1000 * mib));
        Assert.AreEqual(28d, SpeedtestNetProbe.PhaseDeadlineSeconds(60));
        Assert.AreEqual(4d, SpeedtestNetProbe.PhaseDeadlineSeconds(12));
    }

    [TestMethod]
    public async Task MeasureAsync_DefaultWaitsForFifteenSecondsInEachDirection()
    {
        var server = Server();
        var downloadCount = 0;
        var uploadCount = 0;
        var service = new SpeedTestMock
        {
            GetServersAsyncFunc = _ => Task.FromResult<IServer[]>([server]),
            GetFastestServerByLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 2 }),
            GetServerLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 2 }),
            GetDownloadSpeedAsyncFunc = (_, _, _) => { downloadCount++; return Task.FromResult(Sample()); },
            GetUploadSpeedAsyncFunc = (_, _, _) => { uploadCount++; return Task.FromResult(Sample()); }
        };
        var result = await SpeedProbeTestFactory.Create(service).MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 100, 60), CancellationToken.None);
        Assert.AreEqual(SpeedTestState.Success, result.State);
        Assert.AreEqual(8, downloadCount);
        Assert.AreEqual(8, uploadCount);
        Assert.AreEqual(4.194304d, result.DownloadMbps!.Value, 0.000001d);
    }

    [TestMethod]
    public async Task MeasureAsync_BudgetTooSmallForFifteenSecondsIsPartial()
    {
        var server = Server();
        var service = new SpeedTestMock
        {
            GetServersAsyncFunc = _ => Task.FromResult<IServer[]>([server]),
            GetFastestServerByLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 2 }),
            GetServerLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 2 }),
            GetDownloadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Sample()),
            GetUploadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Sample())
        };
        var result = await SpeedProbeTestFactory.Create(service).MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 2, 60), CancellationToken.None);
        Assert.AreEqual(SpeedTestState.Partial, result.State);
        Assert.AreEqual("short_sample", result.DiagnosticCode);
        Assert.IsNotNull(result.DownloadMbps);
        Assert.IsNotNull(result.UploadMbps);
    }

    [TestMethod]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task MeasureAsync_DefaultStartsFourParallelDownloadRequests()
    {
        var handler = new ConcurrentDownloadHandler();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var pending = new SpeedtestNetProbe(new HandlerFactory(handler)).MeasureAsync(
            new(Guid.CreateVersion7(), Guid.CreateVersion7(), 200, 60), cancellation.Token);
        try
        {
            await handler.FourStarted.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.CancellationToken);
        }
        finally
        {
            await cancellation.CancelAsync();
        }
        Assert.AreEqual(SpeedTestState.Cancelled, (await pending).State);
    }

    private sealed class HandlerFactory(ConcurrentDownloadHandler handler) : IHttpMessageHandlerFactory
    {
        public HttpMessageHandler CreateHandler(string name) => handler;
    }

    private sealed class ConcurrentDownloadHandler : HttpMessageHandler
    {
        private int started;
        public TaskCompletionSource FourStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/js/config-sdk")
                return new(HttpStatusCode.OK) { Content = new StringContent("""
                    {"servers":[{"url":"http://speedtest.example.com:8080/speedtest/upload.php","lat":"25","lon":"121","name":"Taipei","country":"Taiwan","sponsor":"Example","id":"1"}]}
                    """), RequestMessage = request };
            if (path.EndsWith("latency.txt", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent("test=test"), RequestMessage = request };
            if (path.EndsWith(".jpg", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref started) == 4) FourStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            throw new InvalidOperationException("Unexpected test request.");
        }
    }
}
