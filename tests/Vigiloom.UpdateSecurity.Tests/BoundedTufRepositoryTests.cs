using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Vigiloom.UpdateSecurity;

namespace Vigiloom.UpdateSecurity.Tests;

[TestClass]
public sealed class BoundedTufRepositoryTests(TestContext context)
{
    private static readonly Uri Metadata = new("https://updates.example.test/metadata/");
    private static readonly Uri Targets = new("https://updates.example.test/targets/");

    [TestMethod]
    public async Task Fetch_UsesFixedPathsAndReturnsMissingMetadata()
    {
        var paths = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return paths.Count == 1 ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var repository = new BoundedTufRepository(Metadata, Targets, client);
        var first = await repository.FetchMetadataAsync("root", 2, context.CancellationToken);
        Assert.IsNotNull(first);
        Assert.AreEqual("{}", Encoding.UTF8.GetString(first));
        Assert.IsNull(await repository.FetchTargetAsync("catalog.json", context.CancellationToken));
        CollectionAssert.AreEqual(new[] { "/metadata/2.root.json", "/targets/catalog.json" }, paths);
    }

    [TestMethod]
    public async Task Fetch_RejectsRedirectAndOversizedResponse()
    {
        using var redirected = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://elsewhere.example.test/") }
        }));
        var repository = new BoundedTufRepository(Metadata, Targets, redirected);
        await Assert.ThrowsAsync<HttpRequestException>(() => repository.FetchMetadataAsync("timestamp", cancellationToken: context.CancellationToken));
        using var tooLarge = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[BoundedTufRepository.MaximumDocumentBytes + 1])
        }));
        repository = new BoundedTufRepository(Metadata, Targets, tooLarge);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.FetchTargetAsync("catalog.json", context.CancellationToken));
    }

    [TestMethod]
    public async Task Fetch_EnforcesLimitWithoutContentLength()
    {
        var stream = new TestStream(BoundedTufRepository.MaximumDocumentBytes + 8192);
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(stream)
        }));
        var repository = new BoundedTufRepository(Metadata, Targets, client);
        await Assert.ThrowsAsync<InvalidDataException>(() => repository.FetchMetadataAsync("snapshot", cancellationToken: context.CancellationToken));
        Assert.IsLessThanOrEqualTo(BoundedTufRepository.MaximumDocumentBytes + 8192, stream.BytesRead);
        Assert.IsTrue(stream.Disposed);
    }

    [TestMethod]
    public void PathsAndOrigins_AreRestricted()
    {
        using var client = new HttpClient(new Handler(_ => throw new AssertFailedException("No request expected.")));
        var repository = new BoundedTufRepository(Metadata, Targets, client);
        Assert.ThrowsExactly<ArgumentException>(() => repository.FetchTargetAsync("../catalog.json", context.CancellationToken));
        Assert.ThrowsExactly<ArgumentException>(() => repository.FetchTargetAsync("https://other.test/catalog.json", context.CancellationToken));
        Assert.ThrowsExactly<ArgumentException>(() => repository.FetchTargetAsync(new string('a', 64) + ".../catalog.json", context.CancellationToken));
        Assert.ThrowsExactly<ArgumentException>(() => repository.FetchTargetAsync(new string('g', 64) + ".json", context.CancellationToken));
        Assert.ThrowsExactly<ArgumentException>(() => repository.FetchMetadataAsync("../root", cancellationToken: context.CancellationToken));
        Assert.ThrowsExactly<ArgumentException>(() => repository.FetchMetadataAsync("root", 0, context.CancellationToken));
        Assert.ThrowsExactly<ArgumentException>(() => new BoundedTufRepository(Metadata, new Uri("https://other.test/"), client));
        Assert.ThrowsExactly<ArgumentException>(() => new BoundedTufRepository(new Uri("http://updates.example.test/"), Targets, client));
    }

    [TestMethod]
    public async Task Fetch_AllowsHashPrefixedCatalogueAndCancellation()
    {
        using var client = new HttpClient(new Handler(request => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") }));
        var repository = new BoundedTufRepository(Metadata, Targets, client);
        Assert.IsNotNull(await repository.FetchTargetAsync(new string('a', 64) + ".catalog.json", context.CancellationToken));
        Assert.IsNotNull(await repository.FetchTargetAsync(new string('a', 64) + ".json", context.CancellationToken));
        Assert.IsNotNull(await repository.FetchTargetAsync(new string('b', 64) + "." + new string('a', 64) + ".json", context.CancellationToken));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => repository.FetchTargetAsync("catalog.json", cancelled.Token));
    }

    [TestMethod]
    public async Task Fetch_CancelsAndDisposesAnIncompleteResponseBody()
    {
        var stream = new TestStream(0, block: true);
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        var repository = new BoundedTufRepository(Metadata, Targets, client, TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAsync<OperationCanceledException>(() => repository.FetchTargetAsync("catalog.json", context.CancellationToken));
        Assert.IsTrue(stream.Disposed);
    }

    [TestMethod]
    public async Task Fetch_SendsAgentCredentialOnlyAsBearerHeader()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.AreEqual("Bearer", request.Headers.Authorization?.Scheme);
            Assert.AreEqual("test-agent-credential", request.Headers.Authorization?.Parameter);
            Assert.DoesNotContain("test-agent-credential", request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        }));
        var repository = new BoundedTufRepository(Metadata, Targets, client, agentCredential: "test-agent-credential");
        Assert.IsNotNull(await repository.FetchMetadataAsync("timestamp", cancellationToken: context.CancellationToken));
        Assert.IsNotNull(await repository.FetchTargetAsync(new string('a', 64) + ".json", context.CancellationToken));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response(request));
        }
    }

    private sealed class TestStream(int size, bool block = false) : Stream
    {
        public int BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (block) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var count = Math.Min(buffer.Length, size - BytesRead);
            BytesRead += count;
            return count;
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { Disposed = true; return base.DisposeAsync(); }
    }
}
