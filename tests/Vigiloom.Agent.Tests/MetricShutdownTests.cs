using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Hosting;
using Vigiloom.Agent.Hosting.Metrics;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MetricShutdownTests(TestContext context)
{
    [TestMethod]
    [DataRow(true, false, false)]
    [DataRow(false, false, false)]
    [DataRow(false, true, false)]
    [DataRow(false, false, true)]
    [Timeout(45000, CooperativeCancellation = true)]
    public async Task Host_StopsWithFullQueueWithoutLosingPersistedBatch(bool releaseSender, bool corruptQueue, bool corruptLedger)
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomMetricShutdownTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        Exception? failure = null;
        var stopped = true;
        try
        {
            var token = context.CancellationToken;
            var vault = new IdentityVault(root);
            var owner = new AgentSettingsOwner(Guid.CreateVersion7(), "https://example.test/", vault.StateDirectory);
            await using (var lease = vault.Acquire())
                await lease.WriteAsync(new IdentityState { Server = owner.Server, AgentId = owner.AgentId, MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            if (corruptLedger)
            {
                await using (var loss = await MetricLossLedger.StartAsync(vault, owner, token)) await loss.CompleteAsync(token);
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "metric-loss.db"), Pooling = false }.ToString());
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "UPDATE loss_state SET unknown = 0.5";
                command.ExecuteNonQuery();
            }
            await new AgentQueueCapacityStore(vault, new Uri(owner.Server), owner.AgentId).SaveAsync(
                AgentQueueCapacitySnapshot.Create(new(1024 * 1024, 1)), null, DateTimeOffset.UtcNow, token);
            var queue = new DurableBatchQueue(vault, owner.AgentId);
            var now = DateTimeOffset.UtcNow;
            var message = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, now, "test", "test", "test");
            var original = new AgentBatch(1, Guid.CreateVersion7(), now, [message]);
            Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(original.BatchId, now, 1, AgentBatchCodec.Serialize(original), token));

            var handler = new HeldFirstBatchHandler();
            using var http = new HttpClient(handler);
            var logger = new CaptureLogger();
            var builder = AgentHost.CreateBuilder([]);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = vault.StateDirectory,
                ["Vigiloom:Agent:Server"] = owner.Server
            });
            foreach (var descriptor in builder.Services.Where(x => x.ServiceType == typeof(IHostedService) &&
                (x.ImplementationType == typeof(AgentSettingsWorker) || x.ImplementationType == typeof(HeartbeatWorker))).ToArray())
                builder.Services.Remove(descriptor);
            builder.Services.AddSingleton(new AgentBatchClient(http, TimeProvider.System));
            builder.Services.AddSingleton<ILogger<MetricWorker>>(logger);
            using var host = builder.Build();
            var settings = host.Services.GetRequiredService<AgentSettingsRuntime>();
            settings.Bind(owner);
            settings.Publish(owner, new AgentAppliedSettings(AgentSettingsSnapshot.Create(AgentSettings.Default with { BatchIntervalSeconds = 1 }),
                now, Guid.CreateVersion7(), owner.AgentId, owner.Server));
            Exception? hostFailure = null;
            try
            {
                stopped = false;
                await host.StartAsync(token);
                await handler.FirstRequest.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                await WaitForAsync(() => logger.Contains("Agent metric queue: Full"), token);
                if (corruptQueue)
                {
                    await using (vault.Acquire()) SetQueueVersion(root, 999);
                }
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var stop = host.StopAsync(deadline.Token);
                if (!corruptQueue)
                {
                    await Task.Delay(250, token);
                    Assert.IsFalse(stop.IsCompleted, "The metric producer should wait for the full queue to drain.");
                }
                if (releaseSender) handler.Release.TrySetResult();
                await stop;
                foreach (var service in host.Services.GetServices<IHostedService>().OfType<BackgroundService>())
                    if (service.ExecuteTask is { } execution) await execution.WaitAsync(deadline.Token);
                stopped = true;
                if (corruptQueue)
                {
                    Assert.IsTrue(logger.Contains("InvalidDataException"));
                    await using (vault.Acquire()) SetQueueVersion(root, 2);
                }
                var pending = await queue.PeekAsync(token);
                if (releaseSender)
                {
                    Assert.IsFalse(logger.Contains("metric batches and"), "Shutdown must not report pending batches left unqueued.");
                    Assert.IsTrue(handler.MetricBatches > 0 || pending is not null && AgentMetricBatchCodec.TryRead(pending.Payload, out _));
                }
                else
                {
                    Assert.IsNotNull(pending);
                    Assert.AreEqual(original.BatchId, pending.BatchId);
                    CollectionAssert.AreEqual(AgentBatchCodec.Serialize(original), pending.Payload);
                    Assert.IsTrue(logger.Matches(@"Agent shutdown left [1-9][0-9]* metric batches and [1-9][0-9]* records not queued"));
                }
                if (corruptLedger)
                {
                    Assert.IsTrue(logger.Contains("loss accounting unavailable; collection continues"));
                    await using var ownership = vault.AcquireMetricOwnership();
                }
                else await using (var ledger = await MetricLossLedger.StartAsync(vault, owner, token))
                {
                    var loss = await ledger.ReadAsync(token);
                    if (releaseSender) Assert.AreEqual(0L, loss.Records);
                    else if (corruptQueue) Assert.IsTrue(loss.CompletenessUnknown, "A failed enqueue cannot be claimed as a known loss.");
                    else Assert.IsGreaterThan(0L, loss.Records, "The confirmed unqueued tail should be recorded on shutdown.");
                    await ledger.CompleteAsync(token);
                }
                Assert.AreEqual(0L, (await queue.GetStatusAsync(token)).QuarantinedBatches);
                Assert.AreEqual(0, handler.ActiveRequests);
                Assert.IsTrue(host.Services.GetServices<IHostedService>().OfType<BackgroundService>().All(x => x.ExecuteTask?.IsCompleted == true));
            }
            catch (Exception error) { hostFailure = error; throw; }
            finally
            {
                try
                {
                    handler.Release.TrySetResult();
                    if (!stopped)
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                        await host.StopAsync(deadline.Token);
                        foreach (var service in host.Services.GetServices<IHostedService>().OfType<BackgroundService>())
                            if (service.ExecuteTask is { } execution) await execution.WaitAsync(deadline.Token);
                        stopped = true;
                    }
                }
                catch (Exception cleanupError) when (hostFailure is not null) { throw new AggregateException(hostFailure, cleanupError); }
            }
        }
        catch (Exception error) { failure = error; throw; }
        finally
        {
            try
            {
                if (!stopped) throw new InvalidOperationException($"Host did not stop; test directory was retained: {root}");
                var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (Exception cleanupError) when (failure is not null) { throw new AggregateException(failure, cleanupError); }
        }
    }

    private static void SetQueueVersion(string root, int version)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "transport.db"), Pooling = false }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = version == 2 ? "PRAGMA user_version = 2" : "PRAGMA user_version = 999";
        command.ExecuteNonQuery();
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(50, timeout.Token);
    }

    private sealed class CaptureLogger : ILogger<MetricWorker>
    {
        private readonly ConcurrentQueue<string> messages = new();
        public bool Contains(string value) => messages.Any(message => message.Contains(value, StringComparison.Ordinal));
        public bool Matches(string pattern) => messages.Any(message => Regex.IsMatch(message, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
    }

    private sealed class HeldFirstBatchHandler : HttpMessageHandler
    {
        private int requests;
        private int active;
        private int metrics;
        public int ActiveRequests => Volatile.Read(ref active);
        public int MetricBatches => Volatile.Read(ref metrics);
        public TaskCompletionSource FirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref active);
            try
            {
                if (Interlocked.Increment(ref requests) == 1)
                {
                    FirstRequest.TrySetResult();
                    await Release.Task.WaitAsync(token);
                }
                var bytes = await request.Content!.ReadAsByteArrayAsync(token);
                if (request.Content.Headers.ContentEncoding.Contains("gzip"))
                {
                    await using var input = new MemoryStream(bytes);
                    await using var gzip = new GZipStream(input, CompressionMode.Decompress);
                    await using var output = new MemoryStream();
                    await gzip.CopyToAsync(output, token);
                    bytes = output.ToArray();
                }
                if (AgentMetricBatchCodec.TryRead(bytes, out _)) Interlocked.Increment(ref metrics);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }
}
