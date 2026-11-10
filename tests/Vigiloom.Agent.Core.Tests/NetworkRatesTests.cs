using Vigiloom.Agent.Core.Metrics;
using System.Net;
using System.Net.Sockets;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class NetworkRatesTests
{
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    public void Update_UsesMatchingConsecutiveInterfacesAndActualElapsedTime()
    {
        var tracker = new NetworkRateTracker(TimeProvider.System);
        var first = new NetworkCounters("a", "old name", 100, 200, 2, 3);
        var start = TimeProvider.System.GetTimestamp();
        var next = start + TimeProvider.System.TimestampFrequency * 2;
        var initial = tracker.Update([first], start, TimeSpan.FromSeconds(60));
        Assert.IsNull(initial[0].ReceivedBytesPerSecond);
        var actual = tracker.Update([first with { Name = "new name", ReceivedBytes = 180, SentBytes = 220, ReceivedErrors = 4 }], next, TimeSpan.FromSeconds(60));
        Assert.AreEqual(40d, actual[0].ReceivedBytesPerSecond);
        Assert.AreEqual(10d, actual[0].SentBytesPerSecond);
        Assert.AreEqual(1d, actual[0].ReceivedErrorsPerSecond);
        Assert.AreEqual(0d, actual[0].SentErrorsPerSecond);
        Assert.AreEqual("new name", actual[0].Name);
    }

    [TestMethod]
    public void Update_ResetMissingOrStaleInterfacesHaveNoFalseRate()
    {
        var tracker = new NetworkRateTracker(TimeProvider.System);
        var start = TimeProvider.System.GetTimestamp();
        var tick = TimeProvider.System.TimestampFrequency;
        var item = new NetworkCounters("a", "adapter", 100, 100, 10, 10);
        tracker.Update([item], start, TimeSpan.FromSeconds(3));
        var reset = tracker.Update([item with { ReceivedBytes = 1, SentBytes = 120 }], start + tick, TimeSpan.FromSeconds(3));
        Assert.IsNull(reset[0].ReceivedBytesPerSecond);
        Assert.AreEqual(20d, reset[0].SentBytesPerSecond);
        tracker.Update([], start + 2 * tick, TimeSpan.FromSeconds(3));
        Assert.IsNull(tracker.Update([item], start + 3 * tick, TimeSpan.FromSeconds(3))[0].SentBytesPerSecond);
        Assert.IsNull(tracker.Update([item], start + 7 * tick, TimeSpan.FromSeconds(3))[0].SentBytesPerSecond);
        tracker.Reset();
        Assert.IsNull(tracker.Update([item], start + 8 * tick, TimeSpan.FromSeconds(3))[0].ReceivedBytesPerSecond);
    }

    [TestMethod]
    public void Sample_ReadsCurrentOperatingSystemInterfaces()
    {
        var sampler = new NetworkSampler(TimeProvider.System);
        var sample = sampler.Sample(TimeSpan.FromMinutes(2));
        Assert.IsLessThanOrEqualTo(NetworkSampler.MaximumInterfaces, sample.Interfaces.Count);
        Assert.IsTrue(sample.Interfaces.All(x => x.ReceivedBytesPerSecond is null && x.SentBytesPerSecond is null));
    }

    [TestMethod]
    public async Task Sample_RecordsControlledLoopbackTraffic()
    {
        // Windows may report zero interface counters for loopback traffic.
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux loopback interface counters."); return; }
        var token = TestContext.CancellationToken;
        var sampler = new NetworkSampler(TimeProvider.System);
        var initial = sampler.Sample(TimeSpan.FromMinutes(2));
        Assert.IsNotEmpty(initial.Interfaces);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, token);
        using var server = await listener.AcceptTcpClientAsync(token);
        var bytes = new byte[256 * 1024];
        var received = new byte[bytes.Length];
        var read = server.GetStream().ReadExactlyAsync(received, token);
        await client.GetStream().WriteAsync(bytes, token);
        await read;
        var next = sampler.Sample(TimeSpan.FromMinutes(2));
        Assert.IsTrue(next.Interfaces.Any(x => x.ReceivedBytesPerSecond > 0 && x.SentBytesPerSecond > 0),
            "No interface reported positive send and receive rates after loopback traffic.");
    }
}
