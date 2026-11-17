using System.Net.Sockets;
using System.Runtime.Versioning;
using Vigiloom.Agent.Core.Updates;
using Vigiloom.BuildingBlocks.Transport;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
[SupportedOSPlatform("linux")]
public sealed class LinuxUpdaterClientTests(TestContext context)
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task CredentialIsSentOnlyAfterKernelConfirmsRootPeer()
    {
        // WSL Windows-mounted directories do not support Unix sockets; a short tmpfs path may be supplied here.
        var directory = Path.Combine(Environment.GetEnvironmentVariable("VIGILOOM_TEST_SOCKET_ROOT") ?? Path.GetTempPath(), $"u-{Guid.NewGuid():N}"[..10]);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "s");
        try
        {
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(1);
            var request = new UpdateHandoff { UpdateId = Guid.CreateVersion7(), Credential = $"vla_{Guid.CreateVersion7():D}.{new string('a', 43)}" };
            var pending = LinuxUpdaterClient.SubmitAsync(path, request, context.CancellationToken);
            using var accepted = await listener.AcceptAsync(context.CancellationToken);
            if (LinuxPeerCredentials.Read(accepted).UserId == 0)
            {
                await using var stream = new NetworkStream(accepted, ownsSocket: false);
                var received = await BoundedJsonFrame.ReadAsync<UpdateHandoff>(stream, context.CancellationToken);
                Assert.AreEqual(request.Credential, received.Credential);
                await BoundedJsonFrame.WriteAsync(stream, new UpdateHandoffResponse { Status = UpdateHandoffStatus.Accepted }, context.CancellationToken);
                Assert.AreEqual(UpdateHandoffStatus.Accepted, await pending);
            }
            else
            {
                await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => pending);
                Assert.AreEqual(0, await accepted.ReceiveAsync(new byte[1], SocketFlags.None, context.CancellationToken));
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
