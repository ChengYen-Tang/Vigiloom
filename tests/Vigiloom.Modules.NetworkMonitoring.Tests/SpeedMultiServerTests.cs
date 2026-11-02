using System.Collections.Concurrent;
using System.Net;
using NetPace.Core;
using NetPace.Core.Clients.Ookla;
using NetPace.Core.Clients.Ookla.Settings;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;

namespace Vigiloom.Modules.NetworkMonitoring.Tests;

[TestClass]
public sealed class SpeedMultiServerTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void AdaptiveConnections_StayBoundedAndNeverShrink()
    {
        Assert.AreEqual(24, SpeedMeasurementMath.NextDownloadConcurrency(4, 4, 40_000_000, 1000));
        Assert.AreEqual(6, SpeedMeasurementMath.NextDownloadConcurrency(4, 1, 40_000_000, 1000));
        Assert.AreEqual(4, SpeedMeasurementMath.NextDownloadConcurrency(4, 4, 1000, 1000));
        Assert.AreEqual(8, SpeedMeasurementMath.NextDownloadConcurrency(8, 4, 1000, 0));
    }

    [TestMethod]
    public async Task RankServers_OrdersLatencyAndExcludesUnreachableCandidates()
    {
        using var client = new HttpClient(new LatencyHandler());
        var settings = new OoklaSpeedtestSettings(Profile.Medium)
        {
            LatencyTest = new LatencyTestSettings { LatencyTestIterations = 1, LatencyTestIntervalMilliseconds = 0 }
        };
        var service = new OoklaSpeedtest(settings, client);
        var servers = Enumerable.Range(1, 4).Select(id => new OoklaServer
        {
            Id = id, Url = $"http://s{id}.example.com:8080/speedtest/upload.php", Location = "City", Sponsor = $"S{id}", Country = "TW"
        }).ToArray();
        var ranked = await SpeedServerDiscovery.RankServersAsync(service, servers, TestContext.CancellationToken);
        CollectionAssert.AreEqual(new[] { 3, 2, 1 }, ranked.Cast<OoklaServer>().Select(server => server.Id).ToArray());
    }

    [TestMethod]
    public async Task RankServers_PropagatesCallerCancellation()
    {
        using var client = new HttpClient(new LatencyHandler());
        var service = new OoklaSpeedtest(new OoklaSpeedtestSettings(Profile.Medium), client);
        var server = new OoklaServer { Id = 1, Url = "http://s1.example.com:8080/speedtest/upload.php", Location = "City", Sponsor = "S1", Country = "TW" };
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => SpeedServerDiscovery.RankServersAsync(service, [server], cancellation.Token));
    }

    private sealed class LatencyHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var host = request.RequestUri!.Host;
            if (host == "s4.example.com") return new(HttpStatusCode.ServiceUnavailable) { RequestMessage = request };
            var delay = host == "s1.example.com" ? 120 : host == "s2.example.com" ? 60 : 10;
            await Task.Delay(delay, token);
            return new(HttpStatusCode.OK) { Content = new StringContent("test=test"), RequestMessage = request };
        }
    }

    [TestMethod]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task AutomaticDownload_UsesFourServersWithSharedConcurrencyAndCancellation()
    {
        var handler = new MultiHandler();
        var probe = new SpeedtestNetProbe(new Factory(handler), 2000, 4, true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var pending = probe.MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 200, 60), cancellation.Token);
        try
        {
            await handler.FourServersStarted.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.CancellationToken);
        }
        finally { await cancellation.CancelAsync(); }
        var result = await pending;
        Assert.AreEqual(SpeedTestState.Cancelled, result.State);
        Assert.HasCount(4, handler.Hosts);
        Assert.AreEqual(4, handler.ActiveWhenAllStarted);
    }

    [TestMethod]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task AutomaticDownload_EnforcesOneSharedTrafficBudget()
    {
        var handler = new MultiHandler(servePayload: true);
        var probe = new SpeedtestNetProbe(new Factory(handler), 2000, 4, true);
        var result = await probe.MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 2, 10), TestContext.CancellationToken);
        Assert.AreEqual(SpeedTestState.BudgetExceeded, result.State);
        Assert.IsNotNull(result.Bytes);
        Assert.IsLessThanOrEqualTo(2L * 1024 * 1024, result.Bytes.Value);
    }

    [TestMethod]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task FixedServers_KeepRequestedOrderAndPrimary()
    {
        var handler = new MultiHandler();
        var probe = new SpeedtestNetProbe(new Factory(handler), 2000, 4, true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var pending = probe.MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 200, 60), null, cancellation.Token,
            multiServerDownload: true,
            fixedDownloadServerIds: [4, 2, 1, 3]);
        try { await handler.FourServersStarted.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.CancellationToken); }
        finally { await cancellation.CancelAsync(); }
        var result = await pending;
        Assert.AreEqual(SpeedTestState.Cancelled, result.State);
        Assert.AreEqual("D (#4) · s4.example.com", result.Server);
        CollectionAssert.AreEquivalent(new[] { "s4.example.com", "s2.example.com", "s1.example.com", "s3.example.com" }, handler.Hosts.Keys.ToArray());
    }

    [TestMethod]
    public async Task FixedServers_MissingIdDoesNotFallBackToDifferentHosts()
    {
        var handler = new MultiHandler();
        var probe = new SpeedtestNetProbe(new Factory(handler), 2000, 4, true);
        var result = await probe.MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 200, 60), null,
            TestContext.CancellationToken, multiServerDownload: true, fixedDownloadServerIds: [1, 5]);
        Assert.AreEqual(SpeedTestState.NoServer, result.State);
        Assert.AreEqual("fixed_server_not_found", result.DiagnosticCode);
        Assert.IsEmpty(handler.Hosts);
    }

    [TestMethod]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task AutomaticDownload_FastServerRestartsWhileSlowServersAreActive()
    {
        var handler = new MultiHandler(uneven: true);
        var probe = new SpeedtestNetProbe(new Factory(handler), 2000, 4, true);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        var pending = probe.MeasureAsync(new(Guid.CreateVersion7(), Guid.CreateVersion7(), 200, 60), null, cancellation.Token,
            multiServerDownload: true);
        bool wasSlowServerActive;
        try { wasSlowServerActive = await handler.FastRestarted.Task.WaitAsync(TimeSpan.FromSeconds(8), TestContext.CancellationToken); }
        finally { await cancellation.CancelAsync(); }
        Assert.IsTrue(wasSlowServerActive, "A fast server should resume without waiting for a slow server.");
        Assert.AreEqual(SpeedTestState.Cancelled, (await pending).State);
    }

    private sealed class Factory(MultiHandler handler) : IHttpMessageHandlerFactory
    {
        public HttpMessageHandler CreateHandler(string name) => handler;
    }

    private sealed class MultiHandler(bool servePayload = false, bool uneven = false) : HttpMessageHandler
    {
        private int active, maxActive;
        private int slowActive, fastRequests;
        // Eight requests remain below the first batch target, forcing the fast
        // server to start a second sub-batch while the slow servers are active.
        private static readonly byte[] UnevenPayload = new byte[32 * 1024];
        public int SlowActive => Volatile.Read(ref slowActive);
        public ConcurrentDictionary<string, byte> Hosts { get; } = new();
        public int MaxActive => Volatile.Read(ref maxActive);
        public int ActiveWhenAllStarted { get; private set; }
        public TaskCompletionSource FourServersStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> FastRestarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath == "/api/js/config-sdk")
                return new(HttpStatusCode.OK) { Content = new StringContent("""
                    {"servers":[
                    {"url":"http://s1.example.com:8080/speedtest/upload.php","lat":"25","lon":"121","name":"A","country":"TW","sponsor":"A","id":"1"},
                    {"url":"http://s2.example.com:8080/speedtest/upload.php","lat":"25","lon":"121","name":"B","country":"TW","sponsor":"B","id":"2"},
                    {"url":"http://s3.example.com:8080/speedtest/upload.php","lat":"25","lon":"121","name":"C","country":"TW","sponsor":"C","id":"3"},
                    {"url":"http://s4.example.com:8080/speedtest/upload.php","lat":"25","lon":"121","name":"D","country":"TW","sponsor":"D","id":"4"}]}
                    """), RequestMessage = request };
            if (uri.AbsolutePath.EndsWith("speedtest-servers-static.php", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent("""
                    <settings><servers>
                    <server url="http://s1.example.com:8080/speedtest/upload.php" lat="25" lon="121" name="A" country="TW" sponsor="A" id="1" />
                    <server url="http://s2.example.com:8080/speedtest/upload.php" lat="25" lon="121" name="B" country="TW" sponsor="B" id="2" />
                    <server url="http://s3.example.com:8080/speedtest/upload.php" lat="25" lon="121" name="C" country="TW" sponsor="C" id="3" />
                    <server url="http://s4.example.com:8080/speedtest/upload.php" lat="25" lon="121" name="D" country="TW" sponsor="D" id="4" />
                    </servers></settings>
                    """), RequestMessage = request };
            if (uri.AbsolutePath.EndsWith("latency.txt", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = new StringContent("test=test"), RequestMessage = request };
            if (uri.AbsolutePath.EndsWith(".jpg", StringComparison.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (uneven)
                {
                    var slow = uri.Host != "s1.example.com";
                    if (!slow && Interlocked.Increment(ref fastRequests) > 8 && SlowActive > 0)
                        FastRestarted.TrySetResult(true);
                    if (slow) Interlocked.Increment(ref slowActive);
                    try { await Task.Delay(slow ? 500 : 10, token); }
                    finally { if (slow) Interlocked.Decrement(ref slowActive); }
                    return new(HttpStatusCode.OK) { Content = new ByteArrayContent(UnevenPayload), RequestMessage = request };
                }
                if (servePayload)
                    return new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[2 * 1024 * 1024]), RequestMessage = request };
                var count = Interlocked.Increment(ref active);
                int observed;
                do { observed = maxActive; if (observed >= count) break; }
                while (Interlocked.CompareExchange(ref maxActive, count, observed) != observed);
                Hosts.TryAdd(uri.Host, 0);
                if (Hosts.Count == 4 && !FourServersStarted.Task.IsCompleted)
                {
                    ActiveWhenAllStarted = count;
                    FourServersStarted.TrySetResult();
                }
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { Interlocked.Decrement(ref active); }
            }
            throw new InvalidOperationException("Unexpected test request.");
        }
    }
}
