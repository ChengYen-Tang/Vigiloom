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
using Vigiloom.Web.AgentPackages;
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
                Sha256 = new string('a', 64), ManifestSha256 = new string('b', 64), Listed = true, VerifiedUntil = DateTimeOffset.UtcNow.AddHours(1) });
            await db.SaveChangesAsync(token);
        }
        using var client = PingTestSite.Client(factory);
        await PingTestSite.LoginAsync(client, "admin", token);
        using var response = await client.GetAsync("/settings/agent-packages?releases=1", token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var html = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token);
        var form = html.QuerySelector($"form[action^='/settings/agent-packages/{id:D}/download']");
        Assert.IsNotNull(form);
        var anti = form.QuerySelector("input[name='__RequestVerificationToken']")!.GetAttribute("value")!;
        using var download = await client.PostAsync(form.GetAttribute("action"),
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = anti }), token);
        Assert.AreEqual(HttpStatusCode.Redirect, download.StatusCode);
        Assert.AreEqual("/settings/agent-packages?releases=1&download=unavailable", download.Headers.Location?.OriginalString);
        Assert.Contains("win-x64", html.Body!.TextContent);
        Assert.Contains("Preview", html.Body.TextContent);
    }, context.CancellationToken);

    [TestMethod]
    public Task Administrator_CanInitializeIndependentlyVerifiedRoot() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        const string setupSecret = "test-only-bootstrap-secret-with-adequate-length";
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:Vigiloom"] = database.ConnectionString, ["Vigiloom:SetupToken"] = setupSecret }));
        });
        var services = factory.Services;
        var identities = services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
        await RoleInitializer.InitializeAsync(identities, token);
        await using (var scope = services.CreateAsyncScope())
            Assert.AreEqual(InitialAdministratorStatus.Created, (await scope.ServiceProvider.GetRequiredService<InitialAdministrator>()
                .CreateAsync(setupSecret, "admin", "Strong-Password-123", token)).Status);

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        using (var loginPage = await client.GetAsync("/account/login", token))
        {
            var document = await new HtmlParser().ParseDocumentAsync(await loginPage.Content.ReadAsStringAsync(token), token);
            var form = document.QuerySelectorAll("form").Single(e => e.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "login");
            var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(e => e.GetAttribute("name")!, e => e.GetAttribute("value") ?? "");
            fields["Input.UserName"] = "admin"; fields["Input.Password"] = "Strong-Password-123";
            using var login = await client.PostAsync("/account/login", new FormUrlEncodedContent(fields), token);
            Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode);
        }
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AgentPackages", "root.json"), token);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var page = await client.GetAsync("/settings/agent-packages", token);
        var html = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token);
        var uploadForm = html.QuerySelector("form[action='/settings/agent-packages/trust']");
        Assert.IsNotNull(uploadForm);
        var anti = uploadForm.QuerySelector("input[name='__RequestVerificationToken']")!.GetAttribute("value")!;

        async Task<HttpResponseMessage> Upload(string hash)
        {
            using var body = new MultipartFormDataContent();
            body.Add(new StringContent(anti), "__RequestVerificationToken");
            body.Add(new ByteArrayContent(bytes), "root", "root.json");
            body.Add(new StringContent(hash), "sha256");
            return await client.PostAsync("/settings/agent-packages/trust", body, token);
        }
        using (var wrong = await Upload(new string('0', 64)))
            Assert.AreEqual("/settings/agent-packages?trust=invalid", wrong.Headers.Location?.OriginalString);
        using (var saved = await Upload(fingerprint.ToUpperInvariant()))
            Assert.AreEqual("/settings/agent-packages?trust=saved", saved.Headers.Location?.OriginalString);
        using (var duplicate = await Upload(fingerprint))
            Assert.AreEqual("/settings/agent-packages?trust=exists", duplicate.Headers.Location?.OriginalString);
        await using var check = await services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token);
        Assert.AreEqual(1, await check.UpdateTrustStates.CountAsync(token));
    }, context.CancellationToken);

    private sealed class ReleaseHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("[{\"tag_name\":\"agent-v1.0.0\",\"draft\":false,\"prerelease\":true}]", System.Text.Encoding.UTF8, "application/json") });
    }
}
