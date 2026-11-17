using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using Vigiloom.BuildingBlocks.Transport;
using Vigiloom.Contracts.Agent;
using Vigiloom.Updater.Core.Transport;

namespace Vigiloom.Updater.Core.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WindowsUpdateListenerTests(TestContext context)
{
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task AcceptsOneRequestAndReturnsBusyUntilItIsConsumed()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!;
        var name = $"vigiloom-test-{Guid.NewGuid():N}";
        await using var listener = new WindowsUpdateListener(sid, name);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var running = listener.RunAsync(stop.Token);
        try
        {
            var request = new UpdateHandoff { UpdateId = Guid.CreateVersion7(), Credential = $"vla_{Guid.CreateVersion7():D}.{new string('a', 43)}" };
            Assert.AreEqual(UpdateHandoffStatus.Accepted, await SendAsync(name, request));
            // The original server keeps the name after serving a request.
            Assert.ThrowsExactly<IOException>(() =>
            {
                using var competing = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance);
            });
            Assert.AreEqual(UpdateHandoffStatus.Busy, await SendAsync(name, request));
            var received = await listener.Requests.ReadAsync(context.CancellationToken);
            Assert.AreEqual(request.UpdateId, received.UpdateId);
            Assert.AreEqual(request.Credential, received.Credential);
        }
        finally { stop.Cancel(); await running; }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task DisconnectedClientDoesNotPreventNextRequest()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!;
        var name = $"vigiloom-test-{Guid.NewGuid():N}";
        await using var listener = new WindowsUpdateListener(sid, name);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var running = listener.RunAsync(stop.Token);
        try
        {
            await using (var disconnected = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await disconnected.ConnectAsync(context.CancellationToken);
                await disconnected.WriteAsync(new byte[] { 0, 0 }, context.CancellationToken);
            }
            var request = new UpdateHandoff { UpdateId = Guid.CreateVersion7(), Credential = $"vla_{Guid.CreateVersion7():D}.{new string('a', 43)}" };
            Assert.AreEqual(UpdateHandoffStatus.Accepted, await SendAsync(name, request));
            Assert.AreEqual(request.UpdateId, (await listener.Requests.ReadAsync(context.CancellationToken)).UpdateId);
        }
        finally { stop.Cancel(); await running; }
    }

    private async Task<UpdateHandoffStatus> SendAsync(string name, UpdateHandoff request)
    {
        const PipeAccessRights rights = PipeAccessRights.ReadData | PipeAccessRights.WriteData | PipeAccessRights.ReadAttributes |
            PipeAccessRights.WriteAttributes | PipeAccessRights.ReadPermissions | PipeAccessRights.Synchronize;
        await using var client = new NamedPipeClientStream(".", name, rights, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification, HandleInheritability.None);
        await client.ConnectAsync(context.CancellationToken);
        await BoundedJsonFrame.WriteAsync(client, request, context.CancellationToken);
        return (await BoundedJsonFrame.ReadAsync<UpdateHandoffResponse>(client, context.CancellationToken)).Status;
    }
}
