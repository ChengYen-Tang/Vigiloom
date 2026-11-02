using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using System.Net;
using System.Net.Http.Json;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Hosting;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class HostLifecycleTests(TestContext testContext)
{
    [TestMethod]
    [DataRow("IdentityClient")]
    [DataRow("AgentHeartbeatClient")]
    [DataRow("AgentBatchClient")]
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
    public async Task Host_BatchSenderHonorsRetryAfterAndRemovesOnlyAcknowledgedBatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomBatchHostTests", Path.GetRandomFileName());
        try
        {
            var token = testContext.CancellationToken;
            var agentId = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState
                {
                    Server = "https://example.test/", AgentId = agentId,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_')
                }, token);
            var now = DateTimeOffset.UtcNow;
            var message = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, now, "Example OS", "X64", ".NET 10");
            var batch = new AgentBatch(1, Guid.CreateVersion7(), now, [message]);
            var queue = new DurableBatchQueue(vault, agentId);
            await queue.EnqueueAsync(batch.BatchId, now, 1, AgentBatchCodec.Serialize(batch), token);
            var handler = new RateLimitedBatchHandler();
            var clock = new ObservedTimeProvider();
            using var http = new HttpClient(handler);
            var builder = AgentHost.CreateBuilder([]);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = root,
                ["Vigiloom:Agent:Server"] = "https://example.test/"
            });
            var heartbeatWorker = builder.Services.Single(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType?.Name == "HeartbeatWorker");
            builder.Services.Remove(heartbeatWorker);
            builder.Services.AddSingleton(new AgentBatchClient(http, clock));
            builder.Services.AddSingleton<TimeProvider>(clock);
            using var host = builder.Build();
            try
            {
                await host.StartAsync(token);
                await clock.FirstScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                clock.Advance(TimeSpan.FromSeconds(6));
                await clock.RetryScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.AreEqual(1, handler.Count);
                Assert.AreEqual(batch.BatchId, (await queue.PeekAsync(token))!.BatchId);
                clock.Advance(TimeSpan.FromSeconds(59));
                Assert.AreEqual(1, handler.Count);
                clock.Advance(TimeSpan.FromSeconds(1));
                await handler.Acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                await clock.NormalScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.IsNull(await queue.PeekAsync(token));
                Assert.AreEqual(2, handler.Count);
            }
            finally { await host.StopAsync(CancellationToken.None); }
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomBatchHostTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task Host_LongRetryAfterRemainsCancellableAndDoesNotSendEarly()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomBatchHostTests", Path.GetRandomFileName());
        try
        {
            var token = testContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState
                {
                    Server = "https://example.test/", AgentId = id,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_')
                }, token);
            var now = DateTimeOffset.UtcNow;
            var item = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, now, "Example OS", "X64", ".NET 10");
            var batch = new AgentBatch(1, Guid.CreateVersion7(), now, [item]);
            var queue = new DurableBatchQueue(vault, id);
            await queue.EnqueueAsync(batch.BatchId, now, 1, AgentBatchCodec.Serialize(batch), token);
            var clock = new ObservedTimeProvider();
            var handler = new RateLimitedBatchHandler(TimeSpan.FromDays(60));
            using var http = new HttpClient(handler);
            var builder = AgentHost.CreateBuilder([]);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = root,
                ["Vigiloom:Agent:Server"] = "https://example.test/"
            });
            var heartbeatWorker = builder.Services.Single(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType?.Name == "HeartbeatWorker");
            builder.Services.Remove(heartbeatWorker);
            builder.Services.AddSingleton(new AgentBatchClient(http, clock));
            builder.Services.AddSingleton<TimeProvider>(clock);
            using var host = builder.Build();
            try
            {
                await host.StartAsync(token);
                await clock.FirstScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                clock.Advance(TimeSpan.FromSeconds(6));
                await clock.FirstDailyScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                clock.Advance(TimeSpan.FromDays(1));
                await clock.SecondDailyScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.AreEqual(1, handler.Count);
                await host.StopAsync(token);
                Assert.AreEqual(batch.BatchId, (await queue.PeekAsync(token))!.BatchId);
            }
            finally { await host.StopAsync(CancellationToken.None); }
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomBatchHostTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task Host_StopCancelsBatchRequestWithoutRemovingPendingBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomBatchHostTests", Path.GetRandomFileName());
        try
        {
            var token = testContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState
                {
                    Server = "https://example.test/", AgentId = id,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_')
                }, token);
            var now = DateTimeOffset.UtcNow;
            var item = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, now, "Example OS", "X64", ".NET 10");
            var batch = new AgentBatch(1, Guid.CreateVersion7(), now, [item]);
            var payload = AgentBatchCodec.Serialize(batch);
            var queue = new DurableBatchQueue(vault, id);
            await queue.EnqueueAsync(batch.BatchId, now, 1, payload, token);
            var clock = new ObservedTimeProvider();
            var handler = new BlockingRequestHandler();
            using var http = new HttpClient(handler);
            var builder = AgentHost.CreateBuilder([]);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = root,
                ["Vigiloom:Agent:Server"] = "https://example.test/"
            });
            var heartbeatWorker = builder.Services.Single(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType?.Name == "HeartbeatWorker");
            builder.Services.Remove(heartbeatWorker);
            builder.Services.AddSingleton(new AgentBatchClient(http, clock));
            builder.Services.AddSingleton<TimeProvider>(clock);
            using var host = builder.Build();
            try
            {
                await host.StartAsync(token);
                await clock.FirstScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                clock.Advance(TimeSpan.FromSeconds(6));
                await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                await host.StopAsync(token);
                await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                var retained = await queue.PeekAsync(token);
                Assert.AreEqual(batch.BatchId, retained!.BatchId);
                CollectionAssert.AreEqual(payload, retained.Payload);
            }
            finally { await host.StopAsync(CancellationToken.None); }
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomBatchHostTests")) + Path.DirectorySeparatorChar;
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
            var handler = new BlockingRequestHandler();
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
            if (request.RequestUri!.AbsolutePath == "/api/v1/agent/queue-session")
                return Task.FromResult(request.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new AgentQueueSession(null)) }
                    : new HttpResponseMessage(HttpStatusCode.NoContent));
            Received.TrySetResult((request.RequestUri, request.Headers.Authorization?.Parameter));
            var number = Interlocked.Increment(ref count);
            if (number == 2) SecondReceived.TrySetResult();
            if (number == 3) ThirdReceived.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private sealed class RateLimitedBatchHandler(TimeSpan? retryAfter = null) : HttpMessageHandler
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public TaskCompletionSource Acknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("/api/v1/agent/batches", request.RequestUri!.AbsolutePath);
            if (Interlocked.Increment(ref count) == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter ?? TimeSpan.FromSeconds(60));
                return Task.FromResult(response);
            }
            Acknowledged.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private sealed class ObservedTimeProvider : TimeProvider
    {
        private readonly FakeTimeProvider inner = new();
        public TaskCompletionSource RetryScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource NormalScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstDailyScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondDailyScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int dailyCount;
        public void Advance(TimeSpan amount) => inner.Advance(amount);
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override long GetTimestamp() => inner.GetTimestamp();
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromDays(1))
            {
                if (Interlocked.Increment(ref dailyCount) == 1) FirstDailyScheduled.TrySetResult();
                else SecondDailyScheduled.TrySetResult();
            }
            else if (dueTime == TimeSpan.FromSeconds(60)) RetryScheduled.TrySetResult();
            else if (RetryScheduled.Task.IsCompleted && dueTime >= TimeSpan.FromSeconds(5) && dueTime < TimeSpan.FromSeconds(6)) NormalScheduled.TrySetResult();
            else if (dueTime >= TimeSpan.FromSeconds(5) && dueTime < TimeSpan.FromSeconds(6)) FirstScheduled.TrySetResult();
            return timer;
        }
    }

    private sealed class BlockingRequestHandler : HttpMessageHandler
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
