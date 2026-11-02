using System.Net;
using System.Net.Http.Headers;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class AgentBatchClientTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Sender_ReportsRetryAfterAndRetainsBytesUntilConfirmed()
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomBatchClientTests", Path.GetRandomFileName());
        try
        {
            var token = TestContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var vault = new IdentityVault(root);
            var secret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = "https://example.test/", AgentId = id, MachineSecret = secret }, token);
            var now = DateTimeOffset.UtcNow;
            var item = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, now, "Example OS", "X64", ".NET 10");
            var batch = new AgentBatch(1, Guid.CreateVersion7(), now, [item]);
            var bytes = AgentBatchCodec.Serialize(batch);
            var queue = new DurableBatchQueue(vault, id);
            Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(batch.BatchId, now, 1, bytes, token));
            var handler = new SequenceHandler();
            using var http = new HttpClient(handler);
            var client = new AgentBatchClient(http, TimeProvider.System);
            var first = await client.SendNextAsync(vault, new Uri("https://example.test/"), token);
            Assert.AreEqual(BatchSendResult.Retry, first.Status);
            Assert.AreEqual(TimeSpan.FromSeconds(60), first.RetryAfter);
            Assert.AreEqual(batch.BatchId, (await queue.PeekAsync(token))!.BatchId);
            var second = await client.SendNextAsync(vault, new Uri("https://example.test/"), token);
            Assert.AreEqual(BatchSendResult.Delivered, second.Status);
            Assert.IsNull(await queue.PeekAsync(token));
            Assert.HasCount(2, handler.Payloads);
            CollectionAssert.AreEqual(bytes, handler.Payloads[0]);
            CollectionAssert.AreEqual(bytes, handler.Payloads[1]);
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomBatchClientTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        public List<byte[]> Payloads { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.AreEqual(HttpMethod.Post, request.Method);
            Assert.AreEqual("/api/v1/agent/batches", request.RequestUri!.AbsolutePath);
            Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
            Payloads.Add(await request.Content!.ReadAsByteArrayAsync(token));
            if (Payloads.Count == 1)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
                return limited;
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
