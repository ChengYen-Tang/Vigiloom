using System.Net;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class WorkspaceIdentityHttpTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task Workspace_UsesCurrentIdentityAndRejectsRevokedSession() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            const string setupSecret = "test-only-bootstrap-secret-with-adequate-length";
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Vigiloom"] = database.ConnectionString,
                    ["Vigiloom:SetupToken"] = setupSecret
                }));
            });
            var contexts = factory.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
            await using (var db = await contexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await RoleInitializer.InitializeAsync(contexts, token);
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var setup = scope.ServiceProvider.GetRequiredService<InitialAdministrator>();
                Assert.AreEqual(InitialAdministratorStatus.Created, (await setup.CreateAsync(setupSecret, "admin", "Strong-Password-123", token)).Status);
                var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
                var reader = new Account { UserName = "reader" };
                Assert.IsTrue((await users.CreateAsync(reader, "Reader-Password-123")).Succeeded);
                Assert.IsTrue((await users.AddToRoleAsync(reader, BuiltInRoles.ReadOnly)).Succeeded);
            }
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
            using (var anonymous = await client.GetAsync("/api/v1/workspace/identity", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using (var anonymousPage = await client.GetAsync("/workspace", token)) Assert.AreEqual(HttpStatusCode.Redirect, anonymousPage.StatusCode);
            async Task Login(string password)
            {
                using var page = await client.GetAsync("/account/login", token);
                var doc = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token);
                var form = doc.QuerySelectorAll("form").Single(e => e.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "login");
                var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(e => e.GetAttribute("name")!, e => e.GetAttribute("value") ?? "");
                fields["Input.UserName"] = "reader"; fields["Input.Password"] = password;
                using var result = await client.PostAsync("/account/login", new FormUrlEncodedContent(fields), token);
                Assert.AreEqual(HttpStatusCode.Redirect, result.StatusCode);
            }
            await Login("Reader-Password-123");
            using (var page = await client.GetAsync("/workspace", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
                Assert.IsTrue(page.Headers.CacheControl?.NoStore);
                var html = await page.Content.ReadAsStringAsync(token);
                Assert.Contains("reader", html);
                Assert.Contains("Read only", html);
                Assert.Contains("data-runtime=\"Static\"", html);
                Assert.DoesNotContain("Reader-Password-123", html);
            }
            using (var response = await client.GetAsync("/api/v1/workspace/identity", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.IsTrue(response.Headers.CacheControl?.NoStore);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                Assert.AreEqual("reader", json.RootElement.GetProperty("userName").GetString());
                Assert.AreEqual("Read only", json.RootElement.GetProperty("roles")[0].GetString());
            }
            using (var denied = await client.GetAsync("/api/v1/accounts", token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
                var reader = await users.FindByNameAsync("reader");
                Assert.IsNotNull(reader);
                Assert.IsTrue((await users.RemoveFromRoleAsync(reader, BuiltInRoles.ReadOnly)).Succeeded);
                Assert.IsTrue((await users.AddToRoleAsync(reader, BuiltInRoles.Administrator)).Succeeded);
                Assert.IsTrue((await users.UpdateSecurityStampAsync(reader)).Succeeded);
            }
            using (var stale = await client.GetAsync("/api/v1/workspace/identity", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, stale.StatusCode);
            using (var stalePage = await client.GetAsync("/workspace", token)) Assert.AreEqual(HttpStatusCode.Redirect, stalePage.StatusCode);
            await Login("Reader-Password-123");
            using var refreshed = await client.GetAsync("/api/v1/workspace/identity", token);
            using var refreshedJson = JsonDocument.Parse(await refreshed.Content.ReadAsStringAsync(token));
            Assert.AreEqual("Administrator", refreshedJson.RootElement.GetProperty("roles")[0].GetString());
        }, TestContext.CancellationToken);
}
