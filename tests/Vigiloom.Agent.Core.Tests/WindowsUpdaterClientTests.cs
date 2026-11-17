using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.Versioning;
using Vigiloom.Agent.Core.Updates;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WindowsUpdaterClientTests(TestContext context)
{
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task UnrelatedPipeServerCannotReceiveCredential()
    {
        var name = $"vigiloom-test-{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var request = new UpdateHandoff { UpdateId = Guid.CreateVersion7(), Credential = $"vla_{Guid.CreateVersion7():D}.{new string('a', 43)}" };
        var pending = WindowsUpdaterClient.SubmitAsync(name, request, context.CancellationToken);
        await server.WaitForConnectionAsync(context.CancellationToken);
        try { await pending; Assert.Fail("An unrelated pipe server must not receive the credential."); }
        catch (Win32Exception) { /* No Updater service is installed on this machine. */ }
        catch (UnauthorizedAccessException) { /* An installed Updater has a different process ID. */ }
        Assert.AreEqual(0, await server.ReadAsync(new byte[1], context.CancellationToken));
    }
}
