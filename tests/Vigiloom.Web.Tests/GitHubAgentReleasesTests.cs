using Vigiloom.Infrastructure.AgentPackages;
using System.Net;
using System.Text;
using Vigiloom.Modules.AgentManagement.Application;

namespace Vigiloom.Web.Tests;

[TestClass]
public sealed class GitHubAgentReleasesTests
{
    [TestMethod]
    public async Task ListsOnlyPublishedAgentReleasesAndDistinguishesPreview()
    {
        using var client = new HttpClient(new ReleasesHandler()) { BaseAddress = new("https://api.github.com/") };
        var page = await new GitHubAgentReleases(client).GetAsync(1, CancellationToken.None);
        Assert.HasCount(2, page.Items);
        Assert.AreEqual("1.0.0", page.Items[0].Version);
        Assert.IsFalse(page.Items[0].Preview);
        Assert.AreEqual("1.1.0-preview.1", page.Items[1].Version);
        Assert.IsTrue(page.Items[1].Preview);
        var asset = page.Items[0].Assets.Single();
        Assert.AreEqual("win-x64", asset.RuntimeIdentifier);
        Assert.AreEqual(new string('a', 64), asset.Sha256);
    }

    private sealed class ReleasesHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.AreEqual("/repos/ChengYen-Tang/Vigiloom/releases?per_page=100&page=1", request.RequestUri!.PathAndQuery);
            const string json = """
                [{"tag_name":"agent-v1.0.0","draft":false,"prerelease":false,"assets":[{"name":"agent-win-x64.zip","id":42,"size":1024,"digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]},
                 {"tag_name":"agent-v1.1.0-preview.1","draft":false,"prerelease":true},
                 {"tag_name":"server-v1.0.0","draft":false,"prerelease":false},
                 {"tag_name":"agent-v2.0.0","draft":true,"prerelease":true}]
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
