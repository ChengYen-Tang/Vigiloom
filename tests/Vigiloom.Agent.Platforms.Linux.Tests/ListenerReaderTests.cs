using System.Net;
using System.Net.Sockets;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Platforms.Linux.Processes;

namespace Vigiloom.Agent.Platforms.Linux.Tests;

[TestClass]
public sealed class ListenerReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void ReadProcessIds_IgnoresPidTextInEscapedProcessNames()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux."); return; }
        var (ids, overflow) = ListenerReader.ReadProcessIds("tcp LISTEN 0 5 127.0.0.1:80 0.0.0.0:* users:((\"evil,pid=999\\\"hidden,pid=777\",pid=123,fd=5))");
        Assert.IsFalse(overflow);
        CollectionAssert.AreEquivalent(new int?[] { 123 }, ids.ToArray());
    }

    [TestMethod]
    public async Task ReadAsync_ReportsLocallyBoundTcpAndUdp()
    {
        if (!OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Linux."); return; }
        if (!File.Exists("/usr/bin/ss")) { Assert.Inconclusive("iproute2 ss is not installed."); return; }
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var tcpPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            var udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            var result = await ListenerReader.ReadAsync(TestContext.CancellationToken);
            Assert.IsTrue(result.Status is InventoryReadStatus.Complete or InventoryReadStatus.Partial);
            Assert.IsTrue(result.Items.Any(x => x.Protocol == "tcp" && x.Port == tcpPort && x.OwningProcessId == Environment.ProcessId));
            Assert.IsTrue(result.Items.Any(x => x.Protocol == "udp" && x.Port == udpPort && x.OwningProcessId == Environment.ProcessId));
        }
        finally { listener.Stop(); }
    }
}
