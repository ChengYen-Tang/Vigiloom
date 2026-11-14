using System.Net;
using System.Security.Cryptography;
using System.Text;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Intelligence;

[TestClass]
public sealed class KevCatalogClientTests
{
    [TestMethod]
    public async Task FetchAsync_UsesOfficialFeedAndHashesContent()
    {
        var bytes = Encoding.UTF8.GetBytes("example");
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.AreEqual("www.cisa.gov", request.RequestUri!.Host);
            Assert.IsTrue(request.Headers.IfNoneMatch.Any());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }));
        var result = await new KevCatalogClient(http).FetchAsync("\"version\"", null, bytes.Length, CancellationToken.None);
        CollectionAssert.AreEqual(bytes, result.Content);
        CollectionAssert.AreEqual(SHA256.HashData(bytes), result.Digest);
    }

    [TestMethod]
    public async Task FetchAsync_RejectsUnconditional304AndOversizeResponse()
    {
        using var notModified = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotModified)));
        await Assert.ThrowsAsync<InvalidDataException>(() => new KevCatalogClient(notModified).FetchAsync(null, null, 10, CancellationToken.None));
        var result = await new KevCatalogClient(notModified).FetchAsync("\"version\"", null, 10, CancellationToken.None);
        Assert.IsNull(result.Content);
        using var oversized = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[11]) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new KevCatalogClient(oversized).FetchAsync(null, null, 10, CancellationToken.None));
    }

    [TestMethod]
    public async Task FetchAsync_RejectsRedirectsAndEncodedResponses()
    {
        using var transport = (HttpClientHandler)IntelligenceHttp.CreateKevHandler();
        Assert.IsFalse(transport.AllowAutoRedirect);
        Assert.AreEqual(DecompressionMethods.None, transport.AutomaticDecompression);
        using var redirect = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)));
        await Assert.ThrowsAsync<HttpRequestException>(() => new KevCatalogClient(redirect).FetchAsync(null, null, 10, CancellationToken.None));
        using var encoded = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
            response.Content.Headers.ContentEncoding.Add("gzip");
            return response;
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => new KevCatalogClient(encoded).FetchAsync(null, null, 10, CancellationToken.None));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
