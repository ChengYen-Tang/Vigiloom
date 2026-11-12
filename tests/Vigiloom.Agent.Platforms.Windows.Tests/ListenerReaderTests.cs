using System.Net;
using System.Net.Sockets;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Platforms.Windows.Processes;

namespace Vigiloom.Agent.Platforms.Windows.Tests;

[TestClass]
public sealed class ListenerReaderTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void ParseOwner_ZeroAndOutOfRangeAreUnknown()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        Assert.IsNull(ListenerReader.ParseOwner(0));
        Assert.IsNull(ListenerReader.ParseOwner(uint.MaxValue));
        Assert.AreEqual(42, ListenerReader.ParseOwner(42));
    }

    [TestMethod]
    public void Read_ReportsTcpAndUdpPortsAndOwningProcess()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Requires Windows."); return; }
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var tcpPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            var udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            var result = ListenerReader.Read(TestContext.CancellationToken);
            Assert.IsTrue(result.Status is InventoryReadStatus.Complete or InventoryReadStatus.Partial);
            Assert.IsTrue(result.Items.Any(x => x.Protocol == "tcp" && x.Port == tcpPort && x.OwningProcessId == Environment.ProcessId));
            Assert.IsTrue(result.Items.Any(x => x.Protocol == "udp" && x.Port == udpPort && x.OwningProcessId == Environment.ProcessId));
        }
        finally { listener.Stop(); }
    }

    [TestMethod]
    public void Read_ReportsIpv6TcpAndUdpPorts()
    {
        if (!OperatingSystem.IsWindows() || !Socket.OSSupportsIPv6) { Assert.Inconclusive("Requires Windows IPv6."); return; }
        var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Start();
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
            var tcpPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            var udpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
            var result = ListenerReader.Read(TestContext.CancellationToken);
            Assert.IsTrue(result.Items.Any(x => x.Protocol == "tcp" && x.Address == "::1" && x.Port == tcpPort && x.OwningProcessId == Environment.ProcessId));
            Assert.IsTrue(result.Items.Any(x => x.Protocol == "udp" && x.Address == "::1" && x.Port == udpPort && x.OwningProcessId == Environment.ProcessId));
        }
        finally { listener.Stop(); }
    }
}
