using System.Net;
using Vigiloom.Modules.SecurityAnalysis.Application.Vulnerabilities;

namespace Vigiloom.Modules.SecurityAnalysis.Tests.Vulnerabilities;

[TestClass]
public sealed class EpssClientTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Fetch_AcceptsOneSameOriginDatedRedirect()
    {
        using var handler = new Handler("epss_scores-2026-11-14.csv.gz");
        using var client = new HttpClient(handler);
        var result = await new EpssClient(client).FetchAsync(1024, TestContext.CancellationToken);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, result);
        Assert.AreEqual(2, handler.Calls);
        Assert.AreEqual("/epss_scores-2026-11-14.csv.gz", handler.Target!.AbsolutePath);
    }

    [TestMethod]
    [DataRow("https://evil.example/epss_scores-2026-11-14.csv.gz")]
    [DataRow("http://epss.empiricalsecurity.com/epss_scores-2026-11-14.csv.gz")]
    [DataRow("https://epss.empiricalsecurity.com:444/epss_scores-2026-11-14.csv.gz")]
    [DataRow("https://user@epss.empiricalsecurity.com/epss_scores-2026-11-14.csv.gz")]
    [DataRow("epss_scores-2026-13-14.csv.gz")]
    [DataRow("epss_scores-2026-11-14.csv.gz?token=test")]
    [DataRow("epss_scores-2026-11-14.csv.gz#fragment")]
    [DataRow("epss_scores-current.csv.gz")]
    public async Task Fetch_RejectsUnexpectedRedirectWithoutSendingSecondRequest(string location)
    {
        using var handler = new Handler(location);
        using var client = new HttpClient(handler);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new EpssClient(client).FetchAsync(1024, TestContext.CancellationToken));
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task Fetch_DoesNotFollowSecondRedirect()
    {
        using var handler = new Handler("epss_scores-2026-11-14.csv.gz") { RedirectAgain = true };
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => new EpssClient(client).FetchAsync(1024, TestContext.CancellationToken));
        Assert.AreEqual(2, handler.Calls);
    }

    private sealed class Handler(string location) : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        internal bool RedirectAgain { get; init; }
        internal Uri? Target { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Target = request.RequestUri;
            var response = new HttpResponseMessage(Calls == 1 || RedirectAgain ? HttpStatusCode.Redirect : HttpStatusCode.OK) { RequestMessage = request };
            if (Calls == 1 || RedirectAgain) response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            else response.Content = new ByteArrayContent([1, 2, 3]);
            return Task.FromResult(response);
        }
    }
}
