using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using System.Net;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Hosting;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class HostLifecycleTests(TestContext testContext)
{
    [TestMethod]
    [DataRow("IdentityClient")]
    [DataRow("AgentHeartbeatClient")]
    public void AgentHttpTransport_DoesNotRedirectOrShareCookies(string name)
    {
        var builder = AgentHost.CreateBuilder([]);
        using var host = builder.Build();
        var factory = host.Services.GetRequiredService<IHttpMessageHandlerFactory>();
        using var pooled = factory.CreateHandler(name);
        HttpMessageHandler handler = pooled;
        while (handler is DelegatingHandler { InnerHandler: { } inner }) handler = inner;
        var sockets = Assert.IsInstanceOfType<SocketsHttpHandler>(handler);
        Assert.IsFalse(sockets.AllowAutoRedirect, "Enrollment secrets must not be replayed to redirect targets.");
        Assert.IsFalse(sockets.UseCookies);
        Assert.AreEqual(TimeSpan.FromMinutes(5), sockets.PooledConnectionLifetime);
    }

    [TestMethod]
    [Timeout(10000, CooperativeCancellation = true)]
    public async Task Host_WithoutProductConfiguration_CanStartAndStop()
    {
        var builder = AgentHost.CreateBuilder([]);
        using var host = builder.Build();
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        await host.StartAsync(testContext.CancellationToken);
        Assert.IsFalse(lifetime.ApplicationStopped.IsCancellationRequested);

        await host.StopAsync(testContext.CancellationToken);
        Assert.IsTrue(lifetime.ApplicationStopped.IsCancellationRequested);
    }

    [TestMethod]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task Host_RegisteredAgentSendsHeartbeatAtStartupAndEveryThirtySeconds()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomHeartbeatHostTests", Path.GetRandomFileName());
        var id = Guid.CreateVersion7();
        var secret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        try
        {
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id, MachineSecret = secret }, testContext.CancellationToken);
            var handler = new RecordingHeartbeatHandler();
            var clock = new FakeTimeProvider();
            using var http = new HttpClient(handler);
            var builder = AgentHost.CreateBuilder([]);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = root,
                ["Vigiloom:Agent:Server"] = "https://example.test/"
            });
            builder.Services.AddSingleton(new AgentHeartbeatClient(http));
            builder.Services.AddSingleton<TimeProvider>(clock);
            using var host = builder.Build();
            try
            {
                await host.StartAsync(testContext.CancellationToken);
                var request = await handler.Received.Task.WaitAsync(TimeSpan.FromSeconds(5), testContext.CancellationToken);
                Assert.AreEqual(new Uri("https://example.test/api/v1/agent/heartbeat"), request.Url);
                Assert.AreEqual($"vla_{id:D}.{secret}", request.Credential);
                clock.Advance(TimeSpan.FromSeconds(29));
                Assert.IsFalse(handler.SecondReceived.Task.IsCompleted);
                clock.Advance(TimeSpan.FromSeconds(1));
                await handler.SecondReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), testContext.CancellationToken);
                clock.Advance(TimeSpan.FromSeconds(30));
                await handler.ThirdReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), testContext.CancellationToken);
            }
            finally { await host.StopAsync(CancellationToken.None); }
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomHeartbeatHostTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task Host_StopCancelsAnInFlightHeartbeat()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomHeartbeatHostTests", Path.GetRandomFileName());
        try
        {
            await using (var session = new IdentityVault(root).Acquire())
                await session.WriteAsync(new IdentityState
                {
                    Server = "https://example.test/", AgentId = Guid.CreateVersion7(),
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_')
                }, testContext.CancellationToken);
            var handler = new BlockingHeartbeatHandler();
            using var http = new HttpClient(handler);
            var builder = AgentHost.CreateBuilder([]);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = root,
                ["Vigiloom:Agent:Server"] = "https://example.test/"
            });
            builder.Services.AddSingleton(new AgentHeartbeatClient(http));
            using var host = builder.Build();
            await host.StartAsync(testContext.CancellationToken);
            try
            {
                await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), testContext.CancellationToken);
                await host.StopAsync(testContext.CancellationToken);
                await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), testContext.CancellationToken);
            }
            finally { await host.StopAsync(CancellationToken.None); }
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomHeartbeatHostTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingHeartbeatHandler : HttpMessageHandler
    {
        public TaskCompletionSource<(Uri? Url, string? Credential)> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ThirdReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Received.TrySetResult((request.RequestUri, request.Headers.Authorization?.Parameter));
            var number = Interlocked.Increment(ref count);
            if (number == 2) SecondReceived.TrySetResult();
            if (number == 3) ThirdReceived.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private sealed class BlockingHeartbeatHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            throw new InvalidOperationException("The blocked heartbeat unexpectedly completed.");
        }
    }
}
