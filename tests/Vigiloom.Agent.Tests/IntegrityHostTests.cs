using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Integrity;
using Vigiloom.Agent.Core.Metrics;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Hosting;
using Vigiloom.Agent.Hosting.Metrics;
using Vigiloom.Contracts.Agent.Integrity;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class IntegrityHostTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task Host_SynchronizesPolicyAndQueuesRealFileSnapshot(bool send)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) { Assert.Inconclusive("Requires Windows or Linux."); return; }
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomIntegrityHostTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        var stopped = true;
        Exception? failure = null;
        try
        {
            var token = TestContext.CancellationToken;
            var files = Path.Combine(root, "files");
            Directory.CreateDirectory(files);
            await File.WriteAllTextAsync(Path.Combine(files, "observed.txt"), "host-test", token);
            var vault = new IdentityVault(Path.Combine(root, "state"));
            var server = new Uri("https://example.test/");
            var agentId = Guid.CreateVersion7();
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = agentId,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var scope = new IntegrityScope(Guid.CreateVersion7(), 1, IntegrityScopeKind.Directory, files, true,
                ImmutableArray<string>.Empty, 60, 100, 1024, 4096, 4);
            var snapshot = IntegritySettingsSnapshot.Create([scope], 1);
            var handler = new Handler(snapshot);
            using var http = new HttpClient(handler);
            var builder = AgentHost.CreateBuilder([]);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:StateDirectory"] = vault.StateDirectory, ["Vigiloom:Agent:Server"] = server.AbsoluteUri
            });
            foreach (var service in builder.Services.Where(x => x.ServiceType == typeof(IHostedService) &&
                x.ImplementationType?.Name is not ("AgentSettingsWorker" or "IntegrityWorker") &&
                (!send || x.ImplementationType?.Name != "BatchWorker")).ToArray()) builder.Services.Remove(service);
            builder.Services.AddSingleton(new AgentSettingsClient(http, TimeProvider.System));
            builder.Services.AddSingleton(new AgentQueueCapacityClient(http, TimeProvider.System));
            builder.Services.AddSingleton(new AgentMetricSettingsClient(http, TimeProvider.System));
            builder.Services.AddSingleton(new AgentLogSettingsClient(http, TimeProvider.System));
            builder.Services.AddSingleton(new AgentInventorySettingsClient(http, TimeProvider.System));
            builder.Services.AddSingleton(new AgentIntegritySettingsClient(http, TimeProvider.System));
            builder.Services.AddSingleton(new IntegrityStatusClient(http, TimeProvider.System));
            builder.Services.AddSingleton(new AgentBatchClient(http, TimeProvider.System));
            builder.Services.AddSingleton<IMetricResourceMonitor>(new Monitor());
            using var host = builder.Build();
            try
            {
                stopped = false;
                await host.StartAsync(token);
                var queue = new DurableBatchQueue(vault, agentId);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(20));
                IntegrityChunk? parsed;
                if (send) parsed = await handler.Delivered.Task.WaitAsync(deadline.Token);
                else
                {
                    QueuedBatch? batch;
                    while ((batch = await queue.PeekAsync(deadline.Token)) is null) await Task.Delay(50, deadline.Token);
                    Assert.IsTrue(IntegrityChunkCodec.TryRead(batch.Payload, out parsed));
                }
                Assert.IsTrue(parsed!.CoverageGap);
                Assert.IsTrue(parsed.Items.Any(x => x.Kind == IntegrityItemKind.File && x.Key.Contains("observed.txt", StringComparison.Ordinal)));
            }
            finally
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await host.StopAsync(timeout.Token);
                foreach (var worker in host.Services.GetServices<IHostedService>().OfType<BackgroundService>())
                    if (worker.ExecuteTask is { } task) await task.WaitAsync(timeout.Token);
                stopped = true;
            }
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            try
            {
                if (!stopped) throw new InvalidOperationException($"Host did not stop; test directory retained: {root}");
                var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("Test cleanup escaped its temporary root.");
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (Exception cleanupError) when (failure is not null) { throw new AggregateException(failure, cleanupError); }
        }
    }

    private sealed class Handler(IntegritySettingsSnapshot snapshot) : HttpMessageHandler
    {
        public TaskCompletionSource<IntegrityChunk> Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/api/v1/agent/integrity-settings")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(snapshot) };
                response.Headers.ETag = new EntityTagHeaderValue($"\"{snapshot.Revision}\"");
                return response;
            }
            if (request.RequestUri.AbsolutePath.StartsWith("/api/v1/agent/integrity-status/", StringComparison.Ordinal))
            {
                var delivered = Delivered.Task.IsCompletedSuccessfully ? Delivered.Task.Result : null;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new IntegrityScopeStatus(
                    snapshot.Scopes[0].Id, true, 1, true, delivered?.Generation ?? 0, null, false, delivered?.Generation,
                    delivered?.Outcome, delivered is null ? null : IntegrityCompletion.Complete, delivered?.SnapshotId, null, delivered?.CoverageGap)) };
            }
            if (request.RequestUri.AbsolutePath == "/api/v1/agent/batches")
            {
                var bytes = await request.Content!.ReadAsByteArrayAsync(token);
                if (request.Content.Headers.ContentEncoding.Contains("gzip"))
                {
                    using var compressed = new MemoryStream(bytes);
                    using var inflater = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    await inflater.CopyToAsync(output, token);
                    bytes = output.ToArray();
                }
                if (!IntegrityChunkCodec.TryRead(bytes, out var parsed)) return new HttpResponseMessage(HttpStatusCode.BadRequest);
                Delivered.TrySetResult(parsed!);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }
    }

    private sealed class Monitor : IMetricResourceMonitor
    {
        public long ReadWorkingSetBytes() => 1;
        public TimeSpan ReadCpuTime() => TimeSpan.Zero;
        public ValueTask<ProcessIoSnapshot> ReadIoAsync(CancellationToken token) => ValueTask.FromResult(new ProcessIoSnapshot(0, 0));
        public void Dispose() { }
    }
}
