using Vigiloom.Infrastructure.AgentPackages;
using System.Net;
using System.Security.Cryptography;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Authentication;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AgentPackageUiTests(TestContext context)
{
    [TestMethod]
    public Task GitHubRelease_ShowsMatchingVerifiedPlatformAndDownload() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var github = new HttpClient(new ReleaseHandler()) { BaseAddress = new Uri("https://api.github.com/") };
        using var factory = PingTestSite.CreateFactory(database.ConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<GitHubAgentReleases>();
                services.AddSingleton(new GitHubAgentReleases(github));
            }));
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        var id = Guid.CreateVersion7();
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
        {
            db.Packages.Add(new AgentPackage { Id = id, Version = "1.0.0", RuntimeIdentifier = "win-x64", Size = 10,
                Sha256 = new string('a', 64), ManifestSha256 = new string('b', 64) });
            await db.SaveChangesAsync(token);
        }
        using var client = PingTestSite.Client(factory);
        await PingTestSite.LoginAsync(client, "admin", token);
        using var response = await client.GetAsync("/settings/agent-packages?releases=1", token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var html = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token);
        var form = html.QuerySelector("form[action='/settings/agent-packages/download']");
        Assert.IsNotNull(form);
        var anti = form.QuerySelector("input[name='__RequestVerificationToken']")!.GetAttribute("value")!;
        using var download = await client.PostAsync(form.GetAttribute("action"),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = anti, ["version"] = "1.0.0", ["runtimeIdentifier"] = "win-x64" }), token);
        Assert.AreEqual(HttpStatusCode.Redirect, download.StatusCode);
        Assert.AreEqual("/settings/agent-packages?releases=1&download=unavailable", download.Headers.Location?.OriginalString);
        Assert.Contains("win-x64", html.Body!.TextContent);
        Assert.Contains("Preview", html.Body.TextContent);
    }, context.CancellationToken);

    private sealed class ReleaseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var json = request.RequestUri!.AbsolutePath.Contains("/tags/", StringComparison.Ordinal)
                ? "{\"tag_name\":\"agent-v1.0.0\",\"draft\":false,\"prerelease\":true,\"assets\":[]}"
                : "[{\"tag_name\":\"agent-v1.0.0\",\"draft\":false,\"prerelease\":true,\"assets\":[{\"name\":\"agent-win-x64.zip\",\"size\":10,\"digest\":\"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}]}]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
