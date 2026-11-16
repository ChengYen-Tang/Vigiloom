using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Updates;
using Vigiloom.Agent.Hosting;
using Vigiloom.BuildingBlocks.Transport;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Tests;

[TestClass]
[SupportedOSPlatform("linux")]
public sealed class AgentUpdateWorkerTests(TestContext context)
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task ChangedServerDuringPollDoesNotConnectToUpdater()
    {
        var parent = Environment.GetEnvironmentVariable("VIGILOOM_TEST_SOCKET_ROOT") ?? Path.GetTempPath();
        var root = Path.Combine(parent, $"w-{Guid.NewGuid():N}"[..10]);
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var stateDirectory = Path.Combine(root, "state");
            var vault = new IdentityVault(stateDirectory);
            var id = Guid.CreateVersion7();
            var secret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id, MachineSecret = secret }, context.CancellationToken);
            var target = new AgentVersionTarget(Guid.CreateVersion7(), Guid.CreateVersion7(), "1.0.0", "linux-x64",
                new string('a', 64), Convert.ToBase64String(new byte[8]));
            var handler = new DelayedHandler(target);
            using var http = new HttpClient(handler);
            var socketPath = Path.Combine(root, "s");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:Server"] = "https://example.test/",
                ["Vigiloom:Agent:StateDirectory"] = stateDirectory,
                ["Vigiloom:Agent:UpdaterSocket"] = socketPath
            }).Build();
            var logger = new StatusLogger();
            using var worker = new AgentUpdateWorker(new AgentVersionClient(http, TimeProvider.System), new AgentSettingsRuntime(),
                configuration, TimeProvider.System, logger);
            await worker.StartAsync(context.CancellationToken);
            try
            {
                await handler.Started.Task.WaitAsync(context.CancellationToken);
                configuration["Vigiloom:Agent:Server"] = "https://other.example/";
                handler.Release.TrySetResult();
                Assert.AreEqual("IdentityChanged", await logger.Status.Task.WaitAsync(context.CancellationToken));
                Assert.IsFalse(listener.Poll(0, SelectMode.SelectRead));
            }
            finally { handler.Release.TrySetResult(); await worker.StopAsync(context.CancellationToken); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task StoppingWorkerCancelsPendingUpdaterResponse()
    {
        if (Environment.GetEnvironmentVariable("VIGILOOM_RUN_ROOT_IPC_TESTS") != "1")
            Assert.Inconclusive("This test requires an isolated Linux root test environment.");
        var parent = Environment.GetEnvironmentVariable("VIGILOOM_TEST_SOCKET_ROOT") ?? Path.GetTempPath();
        var root = Path.Combine(parent, $"w-{Guid.NewGuid():N}"[..10]);
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var id = Guid.CreateVersion7();
            var secret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var stateDirectory = Path.Combine(root, "state");
            var vault = new IdentityVault(stateDirectory);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id, MachineSecret = secret }, context.CancellationToken);
            var target = new AgentVersionTarget(Guid.CreateVersion7(), Guid.CreateVersion7(), "1.0.0", "linux-x64",
                new string('a', 64), Convert.ToBase64String(new byte[8]));
            using var http = new HttpClient(new TargetHandler(target));
            var socketPath = Path.Combine(root, "s");
            using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            listener.Listen(1);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:Server"] = "https://example.test/",
                ["Vigiloom:Agent:StateDirectory"] = stateDirectory,
                ["Vigiloom:Agent:UpdaterSocket"] = socketPath
            }).Build();
            using var worker = new AgentUpdateWorker(new AgentVersionClient(http, TimeProvider.System), new AgentSettingsRuntime(),
                configuration, TimeProvider.System, NullLogger<AgentUpdateWorker>.Instance);
            await worker.StartAsync(context.CancellationToken);
            try
            {
                using var peer = await listener.AcceptAsync(context.CancellationToken);
                await using var stream = new NetworkStream(peer, ownsSocket: false);
                var handoff = await BoundedJsonFrame.ReadAsync<UpdateHandoff>(stream, context.CancellationToken);
                Assert.AreEqual(target.UpdateId, handoff.UpdateId);
                Assert.AreEqual($"vla_{id:D}.{secret}", handoff.Credential);
                await worker.StopAsync(context.CancellationToken);
                Assert.AreEqual(0, await stream.ReadAsync(new byte[1], context.CancellationToken));
            }
            finally { await worker.StopAsync(context.CancellationToken); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class TargetHandler(AgentVersionTarget target) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(target) });
    }

    private sealed class DelayedHandler(AgentVersionTarget target) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(target) };
        }
    }

    private sealed class StatusLogger : ILogger<AgentUpdateWorker>
    {
        public TaskCompletionSource<string> Status { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
                foreach (var value in values)
                    if (value.Key == "Status" && value.Value is string status) Status.TrySetResult(status);
        }
    }
}
