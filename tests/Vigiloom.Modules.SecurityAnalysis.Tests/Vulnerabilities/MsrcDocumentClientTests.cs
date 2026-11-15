using System.Net;
using System.Net.Sockets;
using System.Text;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class MsrcDocumentClientTests
{
    [TestMethod]
    public async Task Fetch_UsesFixedEndpointAndChecksRequestedMonth()
    {
        using var handler = new Handler(MsrcCvrfReaderTests.Example);
        using var client = new HttpClient(handler);
        var upstream = new MsrcDocumentClient(client);
        var result = await upstream.FetchAsync("2025-Nov", 10000, 100, CancellationToken.None);
        Assert.AreEqual("https://api.msrc.microsoft.com/cvrf/v3.0/cvrf/2025-Nov", handler.Uri?.AbsoluteUri);
        Assert.AreEqual("application/json", handler.Accept);
        Assert.AreEqual("2025-Nov", result.Document.Id);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => upstream.FetchAsync("2025-Oct", 10000, 100, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => upstream.FetchAsync("2025-Xxx", 10000, 100, CancellationToken.None));
    }

    [TestMethod]
    public async Task Fetch_RejectsOversizedOrEncodedResponses()
    {
        using var handler = new Handler(MsrcCvrfReaderTests.Example);
        using var client = new HttpClient(handler);
        var upstream = new MsrcDocumentClient(client);
        await Assert.ThrowsAsync<InvalidDataException>(() => upstream.FetchAsync("2025-Nov", 10, 100, CancellationToken.None));
        handler.Encoded = true;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => upstream.FetchAsync("2025-Nov", 10000, 100, CancellationToken.None));
    }

    [TestMethod]
    public async Task Fetch_ObservesCallerCancellation()
    {
        using var handler = new Handler(MsrcCvrfReaderTests.Example);
        using var client = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new MsrcDocumentClient(client).FetchAsync("2025-Nov", 10000, 100, cancellation.Token));
    }

    [TestMethod]
    public async Task Fetch_RejectsRedirectAndUsesNonRedirectingTransport()
    {
        using var transport = (HttpClientHandler)IntelligenceHttp.CreateKevHandler();
        Assert.IsFalse(transport.AllowAutoRedirect);
        Assert.AreEqual(DecompressionMethods.None, transport.AutomaticDecompression);
        using var handler = new Handler(MsrcCvrfReaderTests.Example) { Redirect = true };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => new MsrcDocumentClient(client).FetchAsync("2025-Nov", 10000, 100, CancellationToken.None));
    }

    [TestMethod]
    public async Task Transport_DoesNotFollowRealRedirect()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = socket.GetStream();
            var buffer = new byte[2048];
            Assert.IsGreaterThan(0, await stream.ReadAsync(buffer, deadline.Token));
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{port}/redirected\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, deadline.Token);
        }, deadline.Token);
        var failures = new List<Exception>();
        try
        {
            using var client = new HttpClient(IntelligenceHttp.CreateKevHandler());
            using var response = await client.GetAsync($"http://127.0.0.1:{port}/original", deadline.Token);
            Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
            await server;
            Assert.IsFalse(listener.Pending());
        }
        catch (Exception error) { failures.Add(error); }
        finally
        {
            try { await deadline.CancelAsync(); } catch (Exception error) { failures.Add(error); }
            try { listener.Stop(); } catch (Exception error) { failures.Add(error); }
            try { await server; } catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
            catch (SocketException) when (deadline.IsCancellationRequested) { } catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count != 0) throw new AggregateException(failures);
    }

    private sealed class Handler(string json) : HttpMessageHandler
    {
        public Uri? Uri { get; private set; }
        public string? Accept { get; private set; }
        public bool Encoded { get; set; }
        public bool Redirect { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Accept = request.Headers.Accept.Single().MediaType;
            var response = new HttpResponseMessage(Redirect ? HttpStatusCode.Redirect : HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json)) };
            if (Redirect) response.Headers.Location = new Uri("https://example.invalid/");
            if (Encoded) response.Content.Headers.ContentEncoding.Add("gzip");
            return Task.FromResult(response);
        }
    }
}
