using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Vigiloom.Modules.ServiceObservability.Application.Health;

namespace Vigiloom.Modules.ServiceObservability.Tests.Health;

[TestClass]
public sealed class HealthProbeTests
{
    [TestMethod]
    public async Task ReadAsync_Unhealthy503_ReturnsApplicationState()
    {
        using var client = Client(() => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("Unhealthy", Encoding.UTF8, "text/plain")
        });
        var result = await new HealthProbe(client).ReadAsync(new Uri("https://example.test/health"), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.AreEqual(ReportedHealth.Unhealthy, result.Health);
        Assert.AreEqual(HealthProbeFailure.None, result.Failure);
    }

    [TestMethod]
    [DataRow("application/json", "Healthy")]
    [DataRow("text/plain", "{\"status\":\"Healthy\"}")]
    public async Task ReadAsync_InvalidResponse_DoesNotBecomeHealthy(string mediaType, string body)
    {
        using var client = Client(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, mediaType)
        });
        var result = await new HealthProbe(client).ReadAsync(new Uri("https://example.test/health"), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.IsNull(result.Health);
        Assert.AreEqual(HealthProbeFailure.InvalidResponse, result.Failure);
    }

    [TestMethod]
    public async Task ReadAsync_UnknownLengthOversizedBody_IsRejected()
    {
        using var client = Client(() =>
        {
            var content = new StreamContent(new NonSeekableStream(new byte[1025]));
            content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var result = await new HealthProbe(client).ReadAsync(new Uri("https://example.test/health"), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.AreEqual(HealthProbeFailure.InvalidResponse, result.Failure);
        Assert.IsNull(result.Health);
    }

    [TestMethod]
    public async Task ReadAsync_InvalidUtf8_IsRejectedAndDisposesBody()
    {
        var stream = new TrackedStream([0xff]);
        using var client = Client(() =>
        {
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var result = await new HealthProbe(client).ReadAsync(new Uri("https://example.test/health"), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.AreEqual(HealthProbeFailure.InvalidResponse, result.Failure);
        Assert.IsTrue(stream.Disposed);
    }

    [TestMethod]
    public async Task ReadAsync_StreamFails_ClassifiesTransportAndDisposesBody()
    {
        var stream = new FailingStream();
        using var client = Client(() =>
        {
            var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var result = await new HealthProbe(client).ReadAsync(new Uri("https://example.test/health"), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.AreEqual(HealthProbeFailure.Transport, result.Failure);
        Assert.IsTrue(stream.Disposed);
    }

    [TestMethod]
    public async Task ReadAsync_NonUtf8Charset_IsInvalid()
    {
        using var client = Client(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("Healthy", Encoding.Unicode, "text/plain")
        });
        var result = await new HealthProbe(client).ReadAsync(new Uri("https://example.test/health"), TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.AreEqual(HealthProbeFailure.InvalidResponse, result.Failure);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReadAsync_DeadlineDuringHeadersOrBody_ReturnsTimeout(bool duringBody)
    {
        using var client = new HttpClient(new BlockingHandler(duringBody));
        var result = await new HealthProbe(client).ReadAsync(new Uri("https://example.test/health"), TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.AreEqual(HealthProbeFailure.Timeout, result.Failure);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReadAsync_CallerCancellationDuringHeadersOrBody_Propagates(bool duringBody)
    {
        using var client = new HttpClient(new BlockingHandler(duringBody));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await new HealthProbe(client).ReadAsync(new Uri("https://example.test/health"), TimeSpan.FromSeconds(5), cancellation.Token));
    }

    private static HttpClient Client(Func<HttpResponseMessage> response) => new(new StubHandler(response));

    private sealed class StubHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response());
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    private sealed class TrackedStream(byte[] data) : MemoryStream(data)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { Disposed = true; return base.DisposeAsync(); }
    }

    private sealed class FailingStream : MemoryStream
    {
        public bool Disposed { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("Expected test failure"));
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override ValueTask DisposeAsync() { Disposed = true; return base.DisposeAsync(); }
    }

    private sealed class BlockingHandler(bool duringBody) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!duringBody) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var content = new StreamContent(new BlockingStream());
            content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
