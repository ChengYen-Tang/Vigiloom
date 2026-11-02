using NetPace.Core;
using NetPace.Core.Clients.Ookla;
using NetPace.Core.Clients.Testing;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
public sealed class SpeedProbeSelectionTests
{
    public TestContext TestContext { get; set; } = null!;
    private static OoklaServer Server(int id) => new() { Id = id, Url = "http://speedtest.example.com:8080/speedtest/upload.php", Location = "Taipei", Sponsor = "Example", Country = "Taiwan", Latitude = 25, Longitude = 121 };
    private static SpeedTestResult Sample(long bytes, long duration, int success = 1, int failed = 0) => new()
        { BytesProcessed = bytes, ElapsedMilliseconds = duration, RequestsSucceeded = success, RequestsFailed = failed };

    [TestMethod]
    public async Task RequestedServer_SearchesEntireDirectory()
    {
        var requested = Server(17204);
        var candidates = Enumerable.Range(1, 25).Select(Server).Cast<IServer>().Append(requested).ToArray();
        IServer[]? considered = null;
        var service = new SpeedTestMock
        {
            GetServersAsyncFunc = _ => Task.FromResult(candidates),
            GetFastestServerByLatencyAsyncFunc = (servers, _, _) => { considered = servers; return Task.FromResult(new LatencyTestResult { Server = servers[0], LatencyMilliseconds = 1 }); },
            GetServerLatencyAsyncFunc = (server, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 2 }),
            GetDownloadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Sample(2_000_000, 2000, 2)),
            GetUploadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Sample(1_000_000, 2000, 3))
        };
        var result = await SpeedProbeTestFactory.Create(service, 2000).MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 100, 60), 17204, CancellationToken.None);
        Assert.AreEqual(SpeedTestState.Success, result.State);
        Assert.IsNotNull(considered);
        Assert.HasCount(1, considered);
        Assert.AreSame(requested, considered[0]);

    }

    [TestMethod]
    public async Task RequestedServer_NotFoundDoesNotSelectAnother()
    {
        var service = new SpeedTestMock
        {
            GetServersAsyncFunc = _ => Task.FromResult<IServer[]>([Server(1)]),
            GetFastestServerByLatencyAsyncFunc = (_, _, _) => throw new AssertFailedException("Unexpected server selection")
        };
        var result = await SpeedProbeTestFactory.Create(service, 2000).MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 100, 60), 17204, CancellationToken.None);
        Assert.AreEqual(SpeedTestState.NoServer, result.State);
        Assert.AreEqual("server_not_found", result.DiagnosticCode);
        Assert.IsNull(result.Server);
    }

    [TestMethod]
    public async Task FailedBatch_MakesResultPartial()
    {
        var server = Server(1);
        var service = new SpeedTestMock
        {
            GetServersAsyncFunc = _ => Task.FromResult<IServer[]>([server]),
            GetFastestServerByLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 1 }),
            GetServerLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 1 }),
            GetDownloadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Sample(0, 100, 0, 4)),
            GetUploadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Sample(1_000_000, 2000))
        };
        var result = await SpeedProbeTestFactory.Create(service, 2000).MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 100, 60), null, CancellationToken.None);
        Assert.AreEqual(SpeedTestState.Partial, result.State);
    }

    [TestMethod]
    public async Task CompletedBatchWithFailedRequests_IsNotReportedAsCleanCompletion()
    {
        var server = Server(1);
        var service = new SpeedTestMock
        {
            GetServersAsyncFunc = _ => Task.FromResult<IServer[]>([server]),
            GetFastestServerByLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 1 }),
            GetServerLatencyAsyncFunc = (_, _, _) => Task.FromResult(new LatencyTestResult { Server = server, LatencyMilliseconds = 1 }),
            GetDownloadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Sample(1_000_000, 2000, 1, 2)),
            GetUploadSpeedAsyncFunc = (_, _, _) => Task.FromResult(Sample(1_000_000, 2000))
        };
        var result = await SpeedProbeTestFactory.Create(service, 2000).MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 100, 60), null, CancellationToken.None);
        Assert.AreEqual(SpeedTestState.Partial, result.State);
    }

}

