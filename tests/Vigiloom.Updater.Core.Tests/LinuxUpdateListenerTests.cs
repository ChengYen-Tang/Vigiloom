using System.Runtime.Versioning;
using System.Diagnostics;
using System.Text.Json;
using Vigiloom.Agent.Core.Updates;
using Vigiloom.Contracts.Agent;
using Vigiloom.Updater.Core.Transport;

namespace Vigiloom.Updater.Core.Tests;

[TestClass]
[SupportedOSPlatform("linux")]
public sealed class LinuxUpdateListenerTests(TestContext context)
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task AuthorizedPeerCanQueueOneRequestAndCannotStartSecondListener()
    {
        RequireIsolatedRootTest();
        var directory = CreateDirectory();
        try
        {
            await using var listener = LinuxUpdateListener.Create(directory, agentUid: 0, agentGroupId: 0);
            Assert.ThrowsExactly<IOException>(() => LinuxUpdateListener.Create(directory, agentUid: 0, agentGroupId: 0));
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            var running = listener.RunAsync(stop.Token);
            try
            {
                var request = Request();
                var path = Path.Combine(directory, "updater.sock");
                Assert.AreEqual(UpdateHandoffStatus.Accepted, await LinuxUpdaterClient.SubmitAsync(path, request, context.CancellationToken));
                Assert.AreEqual(UpdateHandoffStatus.Busy, await LinuxUpdaterClient.SubmitAsync(path, Request(), context.CancellationToken));
                var received = await listener.Requests.ReadAsync(context.CancellationToken);
                Assert.AreEqual(request.UpdateId, received.UpdateId);
                Assert.AreEqual(request.Credential, received.Credential);
            }
            finally { stop.Cancel(); await running; }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task UnexpectedPeerIsDisconnectedWithoutEnqueue()
    {
        RequireIsolatedRootTest();
        var directory = CreateDirectory();
        try
        {
            await using var listener = LinuxUpdateListener.Create(directory, agentUid: 12345, agentGroupId: 0);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            var running = listener.RunAsync(stop.Token);
            try
            {
                await Assert.ThrowsAsync<IOException>(() => LinuxUpdaterClient.SubmitAsync(
                    Path.Combine(directory, "updater.sock"), Request(), context.CancellationToken));
                Assert.IsFalse(listener.Requests.TryRead(out _));
            }
            finally { stop.Cancel(); await running; }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task OnlyConfiguredUnprivilegedUserCanSubmitThroughAgentGroup()
    {
        RequireIsolatedRootTest();
        var directory = CreateDirectory();
        try
        {
            using (var chown = Process.Start(new ProcessStartInfo("/usr/bin/chown")
            { ArgumentList = { "0:65534", directory }, UseShellExecute = false })!)
            {
                await chown.WaitForExitAsync(context.CancellationToken);
                Assert.AreEqual(0, chown.ExitCode);
            }
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.SetGroup);
            await using var listener = LinuxUpdateListener.Create(directory, agentUid: 65534, agentGroupId: 65534);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            var running = listener.RunAsync(stop.Token);
            try
            {
                var path = Path.Combine(directory, "updater.sock");
                var request = Request();
                Assert.AreEqual("denied", await SendAsUserAsync(65533, path, request));
                Assert.IsFalse(listener.Requests.TryRead(out _));
                Assert.AreEqual("1", await SendAsUserAsync(65534, path, request));
                Assert.AreEqual(request.UpdateId, (await listener.Requests.ReadAsync(context.CancellationToken)).UpdateId);
            }
            finally { stop.Cancel(); await running; }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private async Task<string> SendAsUserAsync(int uid, string socketPath, UpdateHandoff request)
    {
        const string script = """
            import json, socket, struct, sys
            data = sys.stdin.buffer.read()
            try:
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as s:
                    s.connect(sys.argv[1])
                    s.sendall(struct.pack('>I', len(data)) + data)
                    header = s.recv(4)
                    if len(header) != 4:
                        print('denied')
                    else:
                        length = struct.unpack('>I', header)[0]
                        chunks = bytearray()
                        while len(chunks) < length:
                            block = s.recv(length - len(chunks))
                            if not block: raise ConnectionError('truncated response')
                            chunks.extend(block)
                        print(json.loads(chunks)['status'])
            except (BrokenPipeError, ConnectionResetError, PermissionError):
                print('denied')
            """;
        var start = new ProcessStartInfo("setpriv")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { $"--reuid={uid}", "--regid=65534", "--clear-groups", "/usr/bin/python3", "-c", script, socketPath }
        };
        using var process = Process.Start(start)!;
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(context.CancellationToken);
            await process.WaitForExitAsync(context.CancellationToken);
            Assert.AreEqual(0, process.ExitCode, await error);
            return (await output).Trim();
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cleanupTimeout.Token);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public void GroupWritableIpcDirectoryIsRejected()
    {
        RequireIsolatedRootTest();
        var directory = CreateDirectory();
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.SetGroup);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => LinuxUpdateListener.Create(directory, agentUid: 0, agentGroupId: 0));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task DisposeStopsWaitingListenerAndCompletesQueue()
    {
        RequireIsolatedRootTest();
        var directory = CreateDirectory();
        try
        {
            var listener = LinuxUpdateListener.Create(directory, agentUid: 0, agentGroupId: 0);
            var running = listener.RunAsync(context.CancellationToken);
            await listener.DisposeAsync();
            await running.WaitAsync(context.CancellationToken);
            Assert.IsTrue(listener.Requests.Completion.IsCompleted);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static void RequireIsolatedRootTest()
    {
        if (Environment.GetEnvironmentVariable("VIGILOOM_RUN_ROOT_IPC_TESTS") != "1")
            Assert.Inconclusive("This test requires an isolated Linux root test environment.");
    }

    private static string CreateDirectory()
    {
        var directory = Path.Combine("/run", $"vigiloom-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.SetGroup);
        return directory;
    }

    private static UpdateHandoff Request() => new()
    { UpdateId = Guid.CreateVersion7(), Credential = $"vla_{Guid.CreateVersion7():D}.{new string('a', 43)}" };
}
