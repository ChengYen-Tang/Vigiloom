using System.Net;
using System.Text;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class InventoryCycleTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Run_SnapshotLargerThanQueueReportsFailureInsteadOfWaitingForever()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomInventoryCycleTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = TestContext.CancellationToken;
            var server = new Uri("https://example.test/");
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = id,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var capacity = new AgentQueueCapacity(AgentQueueCapacity.MinimumBytes, 1);
            await new AgentQueueCapacityStore(vault, server, id).SaveAsync(AgentQueueCapacitySnapshot.Create(capacity), null, DateTimeOffset.UtcNow, token);
            using var http = new HttpClient(new StatusHandler());
            var cycle = new InventoryCycle(new InventoryStatusClient(http, TimeProvider.System), TimeProvider.System);
            Task<InventoryReadResult<InventoryEntry>> Collect(InventorySource source, CancellationToken _)
            {
                var items = Enumerable.Range(0, 101).Select(i => new InventoryEntry($"p{i}", $"p{i}", "1", "x64")).ToArray();
                return Task.FromResult(new InventoryReadResult<InventoryEntry>(InventoryReadStatus.Complete, items));
            }
            var result = await cycle.RunAsync(vault, server, InventorySource.LinuxDpkg, Collect, token);
            Assert.AreEqual(InventoryCycleKind.QueuedFailure, result.Kind);
            var batch = await new DurableBatchQueue(vault, id).PeekAsync(token);
            Assert.IsNotNull(batch);
            Assert.IsTrue(InventoryChunkCodec.TryRead(batch.Payload, out var chunk));
            Assert.AreEqual(InventoryOutcome.Error, chunk!.Outcome);
            Assert.IsEmpty(chunk.Items);
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Run_QueuesOnceWaitsAndReconcilesAfterCentralCommit()
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomInventoryCycleTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = TestContext.CancellationToken;
            var server = new Uri("https://example.test/");
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = id,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var handler = new StatusHandler();
            using var http = new HttpClient(handler);
            var cycle = new InventoryCycle(new InventoryStatusClient(http, TimeProvider.System), TimeProvider.System);
            var calls = 0;
            Task<InventoryReadResult<InventoryEntry>> Collect(InventorySource source, CancellationToken _)
            {
                calls++;
                return Task.FromResult(new InventoryReadResult<InventoryEntry>(InventoryReadStatus.Complete,
                    [new("package", "package", "1", "x64")]));
            }
            var result = await cycle.RunAsync(vault, server, InventorySource.LinuxDpkg, Collect, token);
            Assert.AreEqual(InventoryCycleKind.Queued, result.Kind);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(InventoryCycleKind.Waiting, (await cycle.RunAsync(vault, server, InventorySource.LinuxDpkg, Collect, token)).Kind);
            Assert.AreEqual(1, calls);
            var owner = new AgentSettingsOwner(id, server.AbsoluteUri, vault.StateDirectory);
            var outstanding = await new DurableBatchQueue(vault, id).ReadOutstandingInventoryAsync(owner, token);
            Assert.IsNotNull(outstanding);
            handler.Body = $"[{{\"source\":3,\"highWaterGeneration\":1,\"lastAttemptGeneration\":1,\"lastOutcome\":1,\"lastStatus\":2,\"lastSnapshotId\":\"{outstanding.SnapshotId:D}\"}}]";
            Assert.AreEqual(InventoryCycleKind.Complete, (await cycle.RunAsync(vault, server, InventorySource.LinuxDpkg, Collect, token)).Kind);
            Assert.AreEqual(0L, (await new DurableBatchQueue(vault, id).GetStatusAsync(token)).TotalBatches);
            Assert.AreEqual(InventoryCycleKind.Queued, (await cycle.RunAsync(vault, server, InventorySource.LinuxDpkg, Collect, token)).Kind);
            Assert.AreEqual(2, calls);
            // A delayed request from a restored/older local queue can commit the same generation
            // with a different snapshot ID. It must not acknowledge the new local snapshot.
            var otherSnapshot = Guid.CreateVersion7();
            handler.Body = $"[{{\"source\":3,\"highWaterGeneration\":2,\"lastAttemptGeneration\":2,\"lastOutcome\":1,\"lastStatus\":2,\"lastSnapshotId\":\"{otherSnapshot:D}\"}}]";
            Assert.AreEqual(InventoryCycleKind.Rejected, (await cycle.RunAsync(vault, server, InventorySource.LinuxDpkg, Collect, token)).Kind);
            Assert.AreEqual(1L, (await new DurableBatchQueue(vault, id).GetStatusAsync(token)).QuarantinedBatches);
            Assert.AreEqual(InventoryCycleKind.Queued, (await cycle.RunAsync(vault, server, InventorySource.LinuxDpkg, Collect, token)).Kind);
            var third = await new DurableBatchQueue(vault, id).ReadOutstandingInventoryAsync(owner, token);
            Assert.AreEqual(3L, third!.Generation);
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        public string Body { get; set; } = "[]";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
    }
}
