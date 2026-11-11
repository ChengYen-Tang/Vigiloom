using System.Net;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Agent.Core.Configuration;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class InventoryBatchClientTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EmptyInventory_StaysQueuedUntilServerUnderstandsSchema(bool advertisesInventory)
    {
        var parent = Path.Combine(Path.GetTempPath(), "VigiloomInventoryBatchTests");
        var root = Path.Combine(parent, Path.GetRandomFileName());
        try
        {
            var token = TestContext.CancellationToken;
            var agentId = Guid.CreateVersion7();
            var server = new Uri("https://example.test/");
            var vault = new IdentityVault(root);
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = agentId,
                    MachineSecret = Convert.ToBase64String(new byte[32]).TrimEnd('=') }, token);
            var now = DateTimeOffset.UtcNow;
            var measured = InventoryDigest.MeasureChunk([]);
            var chunk = new InventoryChunk(InventoryChunkCodec.SchemaVersion, Guid.CreateVersion7(), now,
                Guid.CreateVersion7(), 1, InventorySource.LinuxDpkg, now, InventoryOutcome.Complete,
                0, 1, 0, measured.Bytes, InventoryDigest.Combine([measured.Digest]), []);
            var bytes = InventoryChunkCodec.Serialize(chunk);
            var queue = new DurableBatchQueue(vault, agentId);
            var owner = new AgentSettingsOwner(agentId, server.AbsoluteUri, vault.StateDirectory);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => queue.EnqueueAsync(chunk.BatchId, now, 1, bytes, token));
            var generic = new[] { new QueuedBatch(chunk.BatchId, now, 1, bytes) };
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => queue.EnqueueManyAsync(generic, token));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => queue.EnqueueManyForOwnerAsync(generic, owner, token));
            var stringVersion = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(bytes)
                .Replace("\"schemaVersion\":4", "\"schemaVersion\":\"4\"", StringComparison.Ordinal));
            Assert.IsTrue(InventoryChunkCodec.TryRead(stringVersion, out _));
            var stringBatch = new[] { new QueuedBatch(chunk.BatchId, now, 1, stringVersion) };
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => queue.EnqueueAsync(chunk.BatchId, now, 1, stringVersion, token));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => queue.EnqueueManyAsync(stringBatch, token));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => queue.EnqueueManyForOwnerAsync(stringBatch, owner, token));
            Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueInventoryForOwnerAsync(
                [new(chunk.BatchId, now, 1, bytes)], owner, chunk.Source, chunk.Generation, chunk.SnapshotId, token));
            using var http = new HttpClient(new BadRequestHandler(advertisesInventory));
            var result = await new AgentBatchClient(http, TimeProvider.System).SendNextAsync(vault, server, token);
            Assert.AreEqual(advertisesInventory ? BatchSendResult.Rejected : BatchSendResult.Retry, result.Status);
            if (advertisesInventory)
            {
                Assert.IsNull(await queue.PeekAsync(token));
                Assert.AreEqual(1, (await queue.GetStatusAsync(token)).QuarantinedBatches);
            }
            else CollectionAssert.AreEqual(bytes, (await queue.PeekAsync(token))!.Payload);
        }
        finally
        {
            var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class BadRequestHandler(bool advertised) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest);
            response.Headers.Add(AgentMetricBatchCodec.SupportedSchemaHeader, advertised ? "1, 2, 3, 4" : "1, 2, 3");
            return Task.FromResult(response);
        }
    }
}
