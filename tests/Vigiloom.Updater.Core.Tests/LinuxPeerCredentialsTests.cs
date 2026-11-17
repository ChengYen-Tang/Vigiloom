using System.Net.Sockets;
using System.Runtime.Versioning;
using Vigiloom.BuildingBlocks.Transport;

namespace Vigiloom.Updater.Core.Tests;

[TestClass]
[SupportedOSPlatform("linux")]
public sealed class LinuxPeerCredentialsTests(TestContext context)
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task ConnectedUnixSocketsReportActualPeerProcess()
    {
        // WSL Windows-mounted directories do not support Unix sockets; a short tmpfs path may be supplied here.
        var directory = Path.Combine(Environment.GetEnvironmentVariable("VIGILOOM_TEST_SOCKET_ROOT") ?? Path.GetTempPath(), $"p-{Guid.NewGuid():N}"[..10]);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "s");
        try
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(new UnixDomainSocketEndPoint(path), context.CancellationToken);
            using var accepted = await listener.AcceptAsync(context.CancellationToken);
            Assert.AreEqual(Environment.ProcessId, LinuxPeerCredentials.Read(client).ProcessId);
            Assert.AreEqual(Environment.ProcessId, LinuxPeerCredentials.Read(accepted).ProcessId);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public void UnconnectedSocketIsRejected()
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        Assert.ThrowsExactly<ArgumentException>(() => LinuxPeerCredentials.Read(socket));
    }
}
