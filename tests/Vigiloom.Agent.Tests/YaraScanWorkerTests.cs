using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Security;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Hosting;
using Vigiloom.Agent.Hosting.Metrics;
using Vigiloom.Contracts.Agent.Security;

namespace Vigiloom.Agent.Tests;

[TestClass]
public sealed class YaraScanWorkerTests(TestContext context)
{
    [TestMethod]
    [Timeout(30000, CooperativeCancellation = true)]
    public async Task Worker_RestartRetainsQueuedResult()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomYaraWorkerTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = context.CancellationToken;
            var vault = new IdentityVault(root);
            var agent = Guid.CreateVersion7();
            var server = new Uri("https://example.test/");
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = agent,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var owner = new AgentSettingsOwner(agent, server.AbsoluteUri, vault.StateDirectory);
            var metrics = new AgentMetricSettingsRuntime(); metrics.Bind(owner);
            var job = new YaraScanJob(Guid.CreateVersion7(), agent, Guid.CreateVersion7(), Guid.CreateVersion7(),
                new(Path.Combine(root, "missing-file"), "rule test { condition: true }", 1024, 10), DateTimeOffset.UtcNow, null);
            var handler = new JobHandler(job);
            using var http = new HttpClient(handler);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Vigiloom:Agent:Server"] = server.AbsoluteUri,
                ["Vigiloom:Agent:StateDirectory"] = vault.StateDirectory
            }).Build();
            var queue = new DurableBatchQueue(vault, agent);
            using (var worker = new YaraScanWorker(new AgentYaraClient(http), new AgentSettingsRuntime(), metrics,
                new SamplingBudgetCoordinator(TimeProvider.System), configuration, TimeProvider.System, NullLogger<YaraScanWorker>.Instance))
            {
                await worker.StartAsync(token);
                try
                {
                    while (await queue.PeekAsync(token) is null) await Task.Delay(50, token);
                }
                finally { await worker.StopAsync(token); }
            }
            var original = (await queue.PeekAsync(token))!;
            Assert.IsTrue(YaraResultBatchCodec.TryRead(original.Payload, out var result));
            Assert.AreEqual(YaraScanOutcome.Unavailable, result!.Result.Outcome);
            using (var restarted = new YaraScanWorker(new AgentYaraClient(http), new AgentSettingsRuntime(), metrics,
                new SamplingBudgetCoordinator(TimeProvider.System), configuration, TimeProvider.System, NullLogger<YaraScanWorker>.Instance))
            {
                await restarted.StartAsync(token);
                try { await handler.SecondRequest.Task.WaitAsync(token); }
                finally { await restarted.StopAsync(token); }
            }
            CollectionAssert.AreEqual(original.Payload, (await queue.PeekAsync(token))!.Payload);
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class JobHandler(YaraScanJob job) : HttpMessageHandler
    {
        private int count;
        public TaskCompletionSource SecondRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref count) >= 2) SecondRequest.TrySetResult();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(job) });
        }
    }
}
