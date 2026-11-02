using System.Net;
using System.Net.Http.Headers;
using System.IO.Compression;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Contracts.Agent;

namespace Vigiloom.Agent.Core.Tests;

[TestClass]
public sealed class AgentBatchClientTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Sender_CompressesBatchAndFallsBackForOlderPlatform(bool rejectGzip, bool cancelBeforeFallback)
    {
        var root = Path.Combine(Path.GetTempPath(), "VigiloomBatchClientTests", Path.GetRandomFileName());
        try
        {
            var token = TestContext.CancellationToken;
            var id = Guid.CreateVersion7();
            var server = new Uri("https://example.test/");
            var vault = new IdentityVault(root);
            var secret = Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            await using (var session = vault.Acquire())
                await session.WriteAsync(new IdentityState { Server = server.AbsoluteUri, AgentId = id, MachineSecret = secret }, token);
            var now = DateTimeOffset.UtcNow;
            var items = Enumerable.Range(0, 12).Select(_ => new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind,
                now, new string('A', 200), "X64", ".NET 10")).ToArray();
            var batch = new AgentBatch(1, Guid.CreateVersion7(), now, items);
            var bytes = AgentBatchCodec.Serialize(batch);
            var queue = new DurableBatchQueue(vault, id);
            Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(batch.BatchId, now, items.Length, bytes, token));
            using var cancellation = new CancellationTokenSource();
            var handler = new CompressionHandler(rejectGzip, cancelBeforeFallback ? cancellation : null);
            using var http = new HttpClient(handler);
            var sender = new AgentBatchClient(http, TimeProvider.System);
            if (cancelBeforeFallback)
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => sender.SendNextAsync(vault, server, cancellation.Token));
                var retained = await queue.PeekAsync(token);
                Assert.AreEqual(batch.BatchId, retained!.BatchId);
                CollectionAssert.AreEqual(bytes, retained.Payload);
                using var retryHttp = new HttpClient(new CompressionHandler(false, null));
                Assert.AreEqual(BatchSendResult.Delivered, (await new AgentBatchClient(retryHttp, TimeProvider.System).SendNextAsync(vault, server, token)).Status);
            }
            else Assert.AreEqual(BatchSendResult.Delivered, (await sender.SendNextAsync(vault, server, token)).Status);
            Assert.IsNull(await queue.PeekAsync(token));
            Assert.HasCount(rejectGzip ? 2 : 1, handler.Payloads);
            Assert.IsTrue(handler.Encodings[0]);
            if (rejectGzip) Assert.IsFalse(handler.Encodings[1]);
            foreach (var observed in handler.Payloads) CollectionAssert.AreEqual(bytes, observed);
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomBatchClientTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Sender_CancellationDuringSuccessfulResponseRetainsBatchForRetry()
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
            await queue.EnqueueAsync(batch.BatchId, now, 1, bytes, token);
            using var cancelled = new CancellationTokenSource();
            var handler = new CancelAfterAckHandler(cancelled);
            using var http = new HttpClient(handler);
            var client = new AgentBatchClient(http, TimeProvider.System);
            await Assert.ThrowsAsync<OperationCanceledException>(() => client.SendNextAsync(vault, new Uri("https://example.test/"), cancelled.Token));
            var retained = await queue.PeekAsync(token);
            Assert.AreEqual(batch.BatchId, retained!.BatchId);
            CollectionAssert.AreEqual(bytes, retained.Payload);
            Assert.AreEqual(BatchSendResult.Delivered, (await client.SendNextAsync(vault, new Uri("https://example.test/"), token)).Status);
            Assert.IsNull(await queue.PeekAsync(token));
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomBatchClientTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Sender_QuarantinesInvalidLocalBatchAndContinuesWithNextBatch()
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
            var queue = new DurableBatchQueue(vault, id);
            var now = DateTimeOffset.UtcNow;
            await queue.EnqueueAsync(Guid.CreateVersion7(), now, 1, "{"u8.ToArray(), token);
            var item = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, now, "Example OS", "X64", ".NET 10");
            var batch = new AgentBatch(1, Guid.CreateVersion7(), now, [item]);
            await queue.EnqueueAsync(batch.BatchId, now, 1, AgentBatchCodec.Serialize(batch), token);
            var handler = new SequenceHandler();
            using var http = new HttpClient(handler);
            var client = new AgentBatchClient(http, TimeProvider.System);
            Assert.AreEqual(BatchSendResult.InvalidLocalBatch, (await client.SendNextAsync(vault, new Uri("https://example.test/"), token)).Status);
            Assert.HasCount(0, handler.Payloads);
            Assert.AreEqual(1L, (await queue.GetStatusAsync(token)).QuarantinedBatches);
            Assert.AreEqual(batch.BatchId, (await queue.PeekAsync(token))!.BatchId);
            Assert.AreEqual(BatchSendResult.Retry, (await client.SendNextAsync(vault, new Uri("https://example.test/"), token)).Status);
        }
        finally
        {
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomBatchClientTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

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

    private sealed class CompressionHandler(bool rejectGzip, CancellationTokenSource? cancelOnRejection) : HttpMessageHandler
    {
        public List<byte[]> Payloads { get; } = [];
        public List<bool> Encodings { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var encoded = request.Content!.Headers.ContentEncoding.Contains("gzip");
            Encodings.Add(encoded);
            var body = await request.Content.ReadAsByteArrayAsync(token);
            if (encoded)
            {
                using var compressed = new MemoryStream(body);
                using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
                using var decoded = new MemoryStream();
                await gzip.CopyToAsync(decoded, token);
                body = decoded.ToArray();
            }
            Payloads.Add(body);
            if (encoded && rejectGzip) cancelOnRejection?.Cancel();
            return new HttpResponseMessage(encoded && rejectGzip ? HttpStatusCode.UnsupportedMediaType : HttpStatusCode.NoContent);
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

    private sealed class CancelAfterAckHandler(CancellationTokenSource cancelled) : HttpMessageHandler
    {
        private bool first = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (first) { first = false; cancelled.Cancel(); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }
}
