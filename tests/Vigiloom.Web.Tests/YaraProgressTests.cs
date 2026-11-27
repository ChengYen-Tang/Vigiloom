using System.Net;
using System.Net.Http.Json;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Agent.Security;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class YaraProgressTests(TestContext context)
{
    [TestMethod]
    public Task ShowsPendingRunningAndCompletedRemoteScans() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        YaraScanJob[] jobs = [];
        using var baseFactory = PingTestSite.CreateFactory(database.ConnectionString);
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["Services:Security:Url"] = "http://security" }));
            builder.ConfigureTestServices(services => services.AddHttpClient("security")
                .ConfigurePrimaryHttpMessageHandler(() => new ScanHandler(() => jobs)));
        });
        var device = await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        var now = DateTimeOffset.UtcNow;
        var agent = Guid.CreateVersion7();
        var actor = Guid.CreateVersion7();
        YaraScanJob Create(string path, DateTimeOffset? started, YaraScanReport? result) =>
            new(Guid.CreateVersion7(), agent, device.Id, actor, new(path, "rule test { condition: true }", 1024, 30), now, result, started);
        jobs = [Create("/pending", null, null), Create("/running", now, null),
            Create("/finished", now, new(YaraScanOutcome.Completed, "", "", false, now))];
        using var reader = PingTestSite.Client(factory);
        await PingTestSite.LoginAsync(reader, "reader", token);
        reader.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        using var response = await reader.GetAsync($"/devices/{device.Id:D}/yara-scans", token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token);
        var articles = document.QuerySelectorAll("article").ToDictionary(x => x.QuerySelector("h3")!.TextContent.Trim());
        Assert.Contains("Waiting for the Agent", articles["/pending"].TextContent);
        Assert.Contains("Scan in progress", articles["/running"].TextContent);
        Assert.Contains("Completed", articles["/finished"].TextContent);
        Assert.DoesNotContain("Scan in progress", articles["/finished"].TextContent);
    }, context.CancellationToken);

    private sealed class ScanHandler(Func<YaraScanJob[]> jobs) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(jobs()) });
    }
}
