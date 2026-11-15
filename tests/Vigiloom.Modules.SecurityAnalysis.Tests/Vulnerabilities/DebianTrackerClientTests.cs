using System.Net;
using System.Net.Sockets;
using System.IO.Compression;
using System.Text;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class DebianTrackerClientTests(TestContext context)
{
    [TestMethod]
    public async Task Fetch_UsesOfficialEndpointAndPreservesBytes()
    {
        var handler = new Handler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("{}"u8.ToArray()) });
        using var client = new HttpClient(handler);
        var result = await new DebianTrackerClient(client).FetchAsync(1024, CancellationToken.None);
        CollectionAssert.AreEqual("{}"u8.ToArray(), result);
        Assert.AreEqual("https://security-tracker.debian.org/tracker/data/json", handler.RequestUri);
        Assert.AreEqual("gzip", handler.Encoding);
    }

    [TestMethod]
    public async Task Fetch_RejectsGzipThatExpandsPastLimit()
    {
        using var compressed = new MemoryStream();
        await using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            await gzip.WriteAsync(new byte[2048], context.CancellationToken);
        var payload = compressed.ToArray();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(context.CancellationToken);
            await using var stream = connection.GetStream();
            var request = new byte[4096];
            await stream.ReadAtLeastAsync(request, 1, cancellationToken: context.CancellationToken);
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Encoding: gzip\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, context.CancellationToken);
            await stream.WriteAsync(payload, context.CancellationToken);
        }, context.CancellationToken);
        using var client = new HttpClient(IntelligenceHttp.CreateDebianHandler());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new VendorSnapshotClient(client, new Uri($"http://127.0.0.1:{port}/"), compressed: true)
            .FetchAsync(1024, context.CancellationToken));
        await server;
    }

    [TestMethod]
    public async Task Fetch_RejectsOversizeRedirectAndUnexpectedEncoding()
    {
        using var oversized = new HttpClient(new Handler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1025]) }));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new DebianTrackerClient(oversized).FetchAsync(1024, CancellationToken.None));
        using var redirect = new HttpClient(new Handler(() => new HttpResponseMessage(HttpStatusCode.Found)));
        await Assert.ThrowsAsync<HttpRequestException>(() => new DebianTrackerClient(redirect).FetchAsync(1024, CancellationToken.None));
        using var encoded = new HttpClient(new Handler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("{}"u8.ToArray()) };
            response.Content.Headers.ContentEncoding.Add("gzip");
            return response;
        }));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new DebianTrackerClient(encoded).FetchAsync(1024, CancellationToken.None));
    }

    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public string? Encoding { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            RequestUri = request.RequestUri?.AbsoluteUri;
            Encoding = request.Headers.AcceptEncoding.ToString();
            return Task.FromResult(response());
        }
    }
}
