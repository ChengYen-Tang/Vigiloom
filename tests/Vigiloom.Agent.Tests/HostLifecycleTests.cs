using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Hosting;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class HostLifecycleTests(TestContext testContext)
{
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task AgentProcess_OfflineRestartRetainsOriginalBatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomAgentProcessTests", Path.GetRandomFileName());
        Exception? primaryFailure = null;
        try
        {
            var token = testContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var server = new Uri("https://127.0.0.1:1/");
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = id,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_') }, token);
            var now = DateTimeOffset.UtcNow;
            var message = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, now, "test", "test", "test");
            var batch = new AgentBatch(1, Guid.CreateVersion7(), now, [message]);
            var bytes = AgentBatchCodec.Serialize(batch);
            var queue = new DurableBatchQueue(vault, id);
            Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(batch.BatchId, now, 1, bytes, token));
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add(typeof(AgentHost).Assembly.Location);
                start.Environment["Vigiloom__Agent__StateDirectory"] = root;
                start.Environment["Vigiloom__Agent__Server"] = server.AbsoluteUri;
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the Agent process.");
                var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
                Exception? processFailure = null;
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(20));
                    string? line;
                    do
                    {
                        line = await process.StandardOutput.ReadLineAsync(deadline.Token);
                        if (line is null) Assert.Fail($"Agent process exited before retry: {await errors.WaitAsync(TimeSpan.FromSeconds(10), deadline.Token)}");
                    } while (!line.Contains("Agent batch transport: Retry", StringComparison.Ordinal));
                }
                catch (Exception error) { processFailure = error; throw; }
                finally
                {
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                        if (!process.WaitForExit(TimeSpan.FromSeconds(10))) throw new TimeoutException("The Agent did not terminate during cleanup.");
                        await errors.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
                    }
                    catch (Exception cleanupError) when (processFailure is not null)
                    {
                        throw new AggregateException("The Agent process test and cleanup both failed.", processFailure, cleanupError);
                    }
                }
                var reopened = new DurableBatchQueue(new IdentityVault(root), id);
                var pending = await reopened.PeekAsync(token);
                Assert.IsNotNull(pending);
                Assert.AreEqual(batch.BatchId, pending.BatchId);
                CollectionAssert.AreEqual(bytes, pending.Payload);
            }
        }
        catch (Exception error) { primaryFailure = error; throw; }
        finally
        {
            try
            {
                var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomAgentProcessTests")) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (Exception cleanupError) when (primaryFailure is not null)
            {
                throw new AggregateException("The test and its cleanup both failed.", primaryFailure, cleanupError);
            }
        }
    }

    [TestMethod]
    [DataRow("IdentityClient")]
    [DataRow("AgentHeartbeatClient")]
    [DataRow("AgentBatchClient")]
    [DataRow("AgentSettingsClient")]
    [DataRow("AgentQueueCapacityClient")]
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
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task Host_SwitchingIdentityDoesNotApplyOrReportPreviousOwnersSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomSettingsOwnerHostTests", Path.GetRandomFileName());
        try
        {
            var token = testContext.CancellationToken;
            var firstPath = Path.Combine(root, "first");
            var secondPath = Path.Combine(root, "second");
            var server = new Uri("https://example.test/");
            var secret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var firstId = Guid.CreateVersion7();
            var secondId = Guid.CreateVersion7();
            var firstVault = new IdentityVault(firstPath);
            await using (var session = firstVault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = firstId, MachineSecret = secret }, token);
            await using (var session = new IdentityVault(secondPath).Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = secondId, MachineSecret = secret }, token);
            var cached = AgentSettingsSnapshot.Create(AgentSettings.Default with { HeartbeatSeconds = 60, BatchIntervalSeconds = 10 });
            await new AgentSettingsStore(firstVault, server, firstId).SaveAsync(cached, null, DateTimeOffset.UtcNow, token);
            var settingsHandler = new AlwaysUnavailableSettingsHandler();
            var heartbeatHandler = new CaptureOwnerHeartbeatHandler();
            var clock = new ObservedTimeProvider();
            using var settingsHttp = new HttpClient(settingsHandler);
            using var heartbeatHttp = new HttpClient(heartbeatHandler);
            using var capacityHttp = new HttpClient(new AlwaysUnavailableSettingsHandler());
            var builder = AgentHost.CreateBuilder([]);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = firstPath,
                ["Vigiloom:Agent:Server"] = server.AbsoluteUri
            });
            builder.Services.Remove(builder.Services.Single(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType?.Name == "BatchWorker"));
            builder.Services.AddSingleton(new AgentSettingsClient(settingsHttp, clock));
            builder.Services.AddSingleton(new AgentQueueCapacityClient(capacityHttp, clock));
            builder.Services.AddSingleton(new AgentHeartbeatClient(heartbeatHttp));
            builder.Services.AddSingleton<TimeProvider>(clock);
            using var host = builder.Build();
            try
            {
                await host.StartAsync(token);
                await clock.SettingsScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.AreEqual(cached.Settings, host.Services.GetRequiredService<AgentSettingsRuntime>().Current);
                await heartbeatHandler.First.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                clock.Advance(TimeSpan.FromSeconds(61));
                await heartbeatHandler.Second.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                builder.Configuration["Vigiloom:Agent:StateDirectory"] = secondPath;
                clock.Advance(TimeSpan.FromSeconds(61));
                heartbeatHandler.ReleaseSecond.TrySetResult();
                var switched = await heartbeatHandler.Third.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.StartsWith($"vla_{secondId:D}.", switched.Credential);
                Assert.IsNull(switched.Report.Configuration?.Applied);
                Assert.AreEqual(AgentSettings.Default, host.Services.GetRequiredService<AgentSettingsRuntime>().Current);
            }
            finally { await host.StopAsync(CancellationToken.None); }
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomSettingsOwnerHostTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task Host_RestoresSettingsOfflineThenAppliesAnUpdatedSnapshot()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomSettingsHostTests", Path.GetRandomFileName());
        try
        {
            var token = testContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            var server = new Uri("https://example.test/");
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = id,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_') }, token);
            var cached = AgentSettingsSnapshot.Create(AgentSettings.Default with { HeartbeatSeconds = 60 });
            await new AgentSettingsStore(vault, server, id).SaveAsync(cached, null, DateTimeOffset.UtcNow, token);
            var next = AgentSettingsSnapshot.Create(AgentSettings.Default with { HeartbeatSeconds = 90, BatchIntervalSeconds = 10 });
            var handler = new SettingsHostHandler(next);
            var clock = new ObservedTimeProvider();
            using var http = new HttpClient(handler);
            using var capacityHttp = new HttpClient(new AlwaysUnavailableSettingsHandler());
            var builder = AgentHost.CreateBuilder([]);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = root,
                ["Vigiloom:Agent:Server"] = server.AbsoluteUri
            });
            foreach (var name in new[] { "HeartbeatWorker", "BatchWorker" })
                builder.Services.Remove(builder.Services.Single(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType?.Name == name));
            builder.Services.AddSingleton(new AgentSettingsClient(http, clock));
            builder.Services.AddSingleton(new AgentQueueCapacityClient(capacityHttp, clock));
            builder.Services.AddSingleton<TimeProvider>(clock);
            using var host = builder.Build();
            try
            {
                await host.StartAsync(token);
                await clock.SettingsScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                var runtime = host.Services.GetRequiredService<AgentSettingsRuntime>();
                Assert.AreEqual(cached.Settings, runtime.Current, "The last valid settings must be active while the server is unavailable.");
                clock.Advance(TimeSpan.FromSeconds(361));
                await handler.Updated.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                await clock.SecondSettingsScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.AreEqual(next.Settings, runtime.Current);
                Assert.AreEqual(next, (await new AgentSettingsStore(vault, server, id).ReadAsync(token))!.Snapshot);
            }
            finally { await host.StopAsync(CancellationToken.None); }
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomSettingsHostTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task Host_RestoresCapacityOfflineAndAppliesServerUpdate()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomCapacityHostTests", Path.GetRandomFileName());
        try
        {
            var token = testContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            var server = new Uri("https://example.test/");
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = id,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_') }, token);
            var old = AgentQueueCapacitySnapshot.Create(new(2L * 1024 * 1024, 20));
            var next = AgentQueueCapacitySnapshot.Create(new(1024L * 1024, 10));
            var store = new AgentQueueCapacityStore(vault, server, id);
            await store.SaveAsync(old, null, DateTimeOffset.UtcNow, token);
            var queue = new DurableBatchQueue(vault, id);
            var clock = new ObservedTimeProvider();
            using var settingsHttp = new HttpClient(new AlwaysUnavailableSettingsHandler());
            using var capacityHttp = new HttpClient(new CapacityHostHandler(next));
            var builder = AgentHost.CreateBuilder([]);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Vigiloom:Agent:StateDirectory"] = root, ["Vigiloom:Agent:Server"] = server.AbsoluteUri });
            foreach (var name in new[] { "HeartbeatWorker", "BatchWorker" })
                builder.Services.Remove(builder.Services.Single(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType?.Name == name));
            builder.Services.AddSingleton(new AgentSettingsClient(settingsHttp, clock));
            builder.Services.AddSingleton(new AgentQueueCapacityClient(capacityHttp, clock));
            builder.Services.AddSingleton<TimeProvider>(clock);
            using var host = builder.Build();
            try
            {
                await host.StartAsync(token);
                await clock.SettingsScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.AreEqual(old, (await store.ReadAsync(token))!.Snapshot);
                clock.Advance(TimeSpan.FromSeconds(361));
                await clock.SecondSettingsScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.AreEqual(next, (await store.ReadAsync(token))!.Snapshot);
                Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(Guid.CreateVersion7(), DateTimeOffset.UtcNow, 1, new byte[1024 * 1024], token));
                Assert.AreEqual(EnqueueResult.Full, await queue.EnqueueAsync(Guid.CreateVersion7(), DateTimeOffset.UtcNow, 1, new byte[1], token));
            }
            finally { await host.StopAsync(CancellationToken.None); }
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomCapacityHostTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CapacityHostHandler(AgentQueueCapacitySnapshot next) : HttpMessageHandler
    {
        private int requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Interlocked.Increment(ref requests) == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(next) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{next.Revision}\"");
            return Task.FromResult(response);
        }
    }

    [TestMethod]
    [Timeout(15000, CooperativeCancellation = true)]
    public async Task Host_HeartbeatUsesTheAppliedInterval()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomHeartbeatHostTests", Path.GetRandomFileName());
        try
        {
            var token = testContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_') }, token);
            var runtime = new AgentSettingsRuntime();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            runtime.Bind(owner);
            runtime.Publish(owner, new AgentAppliedSettings(AgentSettingsSnapshot.Create(AgentSettings.Default with { HeartbeatSeconds = 60 }),
                DateTimeOffset.UtcNow, Guid.CreateVersion7(), id, "https://example.test/"));
            var handler = new RecordingHeartbeatHandler();
            var clock = new FakeTimeProvider();
            using var http = new HttpClient(handler);
            var builder = AgentHost.CreateBuilder([]);
            DisableSettingsWorker(builder);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = root,
                ["Vigiloom:Agent:Server"] = "https://example.test/"
            });
            builder.Services.Remove(builder.Services.Single(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType?.Name == "BatchWorker"));
            builder.Services.AddSingleton(runtime);
            builder.Services.AddSingleton(new AgentHeartbeatClient(http));
            builder.Services.AddSingleton<TimeProvider>(clock);
            using var host = builder.Build();
            try
            {
                await host.StartAsync(token);
                await handler.Received.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                clock.Advance(TimeSpan.FromSeconds(59));
                Assert.IsFalse(handler.SecondReceived.Task.IsCompleted);
                clock.Advance(TimeSpan.FromSeconds(1));
                await handler.SecondReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
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
            DisableSettingsWorker(builder);
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
    public async Task Host_TransientIdentityLeaseContentionRetainsLastGoodSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomBatchHostTests", Path.GetRandomFileName());
        try
        {
            var token = testContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_') }, token);
            var runtime = new AgentSettingsRuntime();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            runtime.Bind(owner);
            var configured = AgentSettingsSnapshot.Create(AgentSettings.Default with { BatchIntervalSeconds = 10 });
            runtime.Publish(owner, new AgentAppliedSettings(configured, DateTimeOffset.UtcNow, Guid.CreateVersion7(), id, owner.Server));
            var clock = new ObservedTimeProvider();
            using var http = new HttpClient(new AcceptBatchHandler());
            var builder = AgentHost.CreateBuilder([]);
            DisableSettingsWorker(builder);
            builder.Services.Remove(builder.Services.Single(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType?.Name == "HeartbeatWorker"));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = root,
                ["Vigiloom:Agent:Server"] = owner.Server
            });
            builder.Services.AddSingleton(runtime);
            builder.Services.AddSingleton(new AgentBatchClient(http, clock));
            builder.Services.AddSingleton<TimeProvider>(clock);
            using var host = builder.Build();
            IdentityVault.Session? held = vault.Acquire();
            try
            {
                await host.StartAsync(token);
                await clock.FirstScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.AreEqual(configured.Settings, runtime.Current, "A busy lease must not erase last-good settings.");
                await held.DisposeAsync();
                held = null;
                clock.Advance(TimeSpan.FromSeconds(6));
                await clock.ConfiguredBatchScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.AreEqual(configured.Settings, runtime.Current);
            }
            finally
            {
                if (held is not null) await held.DisposeAsync();
                await host.StopAsync(CancellationToken.None);
            }
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
    public async Task Host_BatchSenderUsesTheAppliedInterval()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomBatchHostTests", Path.GetRandomFileName());
        try
        {
            var token = testContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_') }, token);
            var now = DateTimeOffset.UtcNow;
            var item = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, now, "Example OS", "X64", ".NET 10");
            var batch = new AgentBatch(1, Guid.CreateVersion7(), now, [item]);
            var queue = new DurableBatchQueue(vault, id);
            await queue.EnqueueAsync(batch.BatchId, now, 1, AgentBatchCodec.Serialize(batch), token);
            var runtime = new AgentSettingsRuntime();
            var owner = new AgentSettingsOwner(id, "https://example.test/", vault.StateDirectory);
            runtime.Bind(owner);
            runtime.Publish(owner, new AgentAppliedSettings(AgentSettingsSnapshot.Create(AgentSettings.Default with { BatchIntervalSeconds = 10 }),
                now, Guid.CreateVersion7(), id, "https://example.test/"));
            var handler = new AcceptBatchHandler();
            var clock = new ObservedTimeProvider();
            using var http = new HttpClient(handler);
            var builder = AgentHost.CreateBuilder([]);
            DisableSettingsWorker(builder);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = root,
                ["Vigiloom:Agent:Server"] = "https://example.test/"
            });
            builder.Services.Remove(builder.Services.Single(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType?.Name == "HeartbeatWorker"));
            builder.Services.AddSingleton(runtime);
            builder.Services.AddSingleton(new AgentBatchClient(http, clock));
            builder.Services.AddSingleton<TimeProvider>(clock);
            using var host = builder.Build();
            try
            {
                await host.StartAsync(token);
                await clock.ConfiguredBatchScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                clock.Advance(TimeSpan.FromSeconds(9));
                Assert.IsFalse(handler.Delivered.Task.IsCompleted);
                clock.Advance(TimeSpan.FromSeconds(3));
                await handler.Delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
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
            DisableSettingsWorker(builder);
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
                await clock.BurstScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                clock.Advance(TimeSpan.FromMilliseconds(250));
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
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task Host_BatchSenderCatchesUpInBoundedBursts()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomBatchHostTests", Path.GetRandomFileName());
        try
        {
            var token = testContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_') }, token);
            var now = DateTimeOffset.UtcNow;
            var queue = new DurableBatchQueue(vault, id);
            for (var i = 0; i < 5; i++)
            {
                var item = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, now, "Example OS", "X64", ".NET 10");
                var batch = new AgentBatch(1, Guid.CreateVersion7(), now, [item]);
                Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(batch.BatchId, now, 1, AgentBatchCodec.Serialize(batch), token));
            }
            var clock = new ObservedTimeProvider();
            var handler = new BatchBurstHandler();
            using var http = new HttpClient(handler);
            var builder = AgentHost.CreateBuilder([]);
            DisableSettingsWorker(builder);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = root,
                ["Vigiloom:Agent:Server"] = "https://example.test/"
            });
            builder.Services.Remove(builder.Services.Single(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType?.Name == "HeartbeatWorker"));
            builder.Services.AddSingleton(new AgentBatchClient(http, clock));
            builder.Services.AddSingleton<TimeProvider>(clock);
            using var host = builder.Build();
            try
            {
                await host.StartAsync(token);
                await clock.FirstScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                clock.Advance(TimeSpan.FromSeconds(6));
                for (var i = 0; i < 3; i++)
                {
                    Assert.IsTrue(await clock.BurstTimers.WaitAsync(TimeSpan.FromSeconds(5), token));
                    clock.Advance(TimeSpan.FromMilliseconds(250));
                }
                await clock.BurstPauseScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.AreEqual(4, handler.Count);
                Assert.IsNotNull(await queue.PeekAsync(token));
                clock.Advance(TimeSpan.FromSeconds(6));
                await handler.Fifth.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.IsTrue(await clock.BurstTimers.WaitAsync(TimeSpan.FromSeconds(5), token));
                Assert.IsNull(await queue.PeekAsync(token));
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
    [Timeout(20000, CooperativeCancellation = true)]
    public async Task Host_BatchSenderHonorsRetryAfterDuringCatchUp()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomBatchHostTests", Path.GetRandomFileName());
        try
        {
            var token = testContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_') }, token);
            var now = DateTimeOffset.UtcNow;
            var queue = new DurableBatchQueue(vault, id);
            for (var i = 0; i < 2; i++)
            {
                var item = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, now, "Example OS", "X64", ".NET 10");
                var batch = new AgentBatch(1, Guid.CreateVersion7(), now, [item]);
                Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(batch.BatchId, now, 1, AgentBatchCodec.Serialize(batch), token));
            }
            var clock = new ObservedTimeProvider();
            var handler = new CatchUpRateLimitHandler();
            using var http = new HttpClient(handler);
            var builder = AgentHost.CreateBuilder([]);
            DisableSettingsWorker(builder);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = root,
                ["Vigiloom:Agent:Server"] = "https://example.test/"
            });
            builder.Services.Remove(builder.Services.Single(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType?.Name == "HeartbeatWorker"));
            builder.Services.AddSingleton(new AgentBatchClient(http, clock));
            builder.Services.AddSingleton<TimeProvider>(clock);
            using var host = builder.Build();
            try
            {
                await host.StartAsync(token);
                await clock.FirstScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                clock.Advance(TimeSpan.FromSeconds(6));
                Assert.IsTrue(await clock.BurstTimers.WaitAsync(TimeSpan.FromSeconds(5), token));
                clock.Advance(TimeSpan.FromMilliseconds(250));
                await clock.RetryScheduled.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.AreEqual(2, handler.Count);
                var pending = await queue.PeekAsync(token);
                Assert.IsNotNull(pending);
                clock.Advance(TimeSpan.FromSeconds(59));
                Assert.AreEqual(2, handler.Count);
                clock.Advance(TimeSpan.FromSeconds(1));
                await handler.Retried.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                Assert.IsTrue(await clock.BurstTimers.WaitAsync(TimeSpan.FromSeconds(5), token));
                Assert.IsNull(await queue.PeekAsync(token));
                Assert.AreEqual(3, handler.Count);
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
            DisableSettingsWorker(builder);
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
            DisableSettingsWorker(builder);
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
            DisableSettingsWorker(builder);
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

    private static void DisableSettingsWorker(HostApplicationBuilder builder)
    {
        var descriptor = builder.Services.Single(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType?.Name == "AgentSettingsWorker");
        builder.Services.Remove(descriptor);
    }

    private sealed class AlwaysUnavailableSettingsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }

    private sealed class CaptureOwnerHeartbeatHandler : HttpMessageHandler
    {
        private int count;
        public TaskCompletionSource First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Second { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSecond { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<(string Credential, AgentHeartbeatReport Report)> Third { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/agent/queue-session")
                return request.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new AgentQueueSession(null)) }
                    : new HttpResponseMessage(HttpStatusCode.NoContent);
            var report = (await request.Content!.ReadFromJsonAsync<AgentHeartbeatReport>(cancellationToken: cancellationToken))!;
            var number = Interlocked.Increment(ref count);
            if (number == 1) First.TrySetResult();
            if (number == 2)
            {
                Second.TrySetResult();
                await ReleaseSecond.Task.WaitAsync(cancellationToken);
            }
            if (number == 3) Third.TrySetResult((request.Headers.Authorization!.Parameter!, report));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private sealed class AcceptBatchHandler : HttpMessageHandler
    {
        public TaskCompletionSource Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("/api/v1/agent/batches", request.RequestUri!.AbsolutePath);
            Delivered.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private sealed class BatchBurstHandler : HttpMessageHandler
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public TaskCompletionSource Fifth { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref count) == 5) Fifth.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private sealed class CatchUpRateLimitHandler : HttpMessageHandler
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public TaskCompletionSource Retried { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var number = Interlocked.Increment(ref count);
            if (number == 2)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
                return Task.FromResult(response);
            }
            if (number == 3) Retried.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private sealed class SettingsHostHandler(AgentSettingsSnapshot next) : HttpMessageHandler
    {
        private int count;
        public TaskCompletionSource Updated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("/api/v1/agent/settings", request.RequestUri!.AbsolutePath);
            Assert.IsNotNull(request.Headers.Authorization);
            Assert.HasCount(1, request.Headers.IfNoneMatch);
            if (Interlocked.Increment(ref count) == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(next) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{next.Revision}\"");
            Updated.TrySetResult();
            return Task.FromResult(response);
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
        public TaskCompletionSource BurstScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BurstPauseScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SemaphoreSlim BurstTimers { get; } = new(0);
        public TaskCompletionSource FirstScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstDailyScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondDailyScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SettingsScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondSettingsScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ConfiguredBatchScheduled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int dailyCount;
        private int settingsCount;
        private int burstCount;
        public void Advance(TimeSpan amount) => inner.Advance(amount);
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override long GetTimestamp() => inner.GetTimestamp();
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromMilliseconds(250))
            {
                Interlocked.Increment(ref burstCount);
                BurstScheduled.TrySetResult();
                BurstTimers.Release();
            }
            if (dueTime >= TimeSpan.FromSeconds(5) && dueTime <= TimeSpan.FromSeconds(6) && Volatile.Read(ref burstCount) >= 3)
                BurstPauseScheduled.TrySetResult();
            if (dueTime >= TimeSpan.FromSeconds(10) && dueTime <= TimeSpan.FromSeconds(12)) ConfiguredBatchScheduled.TrySetResult();
            if (dueTime >= TimeSpan.FromSeconds(240) && dueTime <= TimeSpan.FromSeconds(360))
            {
                if (Interlocked.Increment(ref settingsCount) == 1) SettingsScheduled.TrySetResult();
                else SecondSettingsScheduled.TrySetResult();
            }
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
