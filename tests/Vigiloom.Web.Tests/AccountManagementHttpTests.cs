using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AccountManagementHttpTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task AdministratorCanCreateAccount_ReaderCanResetAndChangePassword_ButCannotManage() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            const string setupSecret = "test-only-bootstrap-secret-with-adequate-length";
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Vigiloom"] = database.ConnectionString,
                    ["Vigiloom:SetupToken"] = setupSecret,
                    ["Vigiloom:Authentication:RequestsPerMinute"] = "100"
                }));
            });
            var contexts = factory.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
            await using (var db = await contexts.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await RoleInitializer.InitializeAsync(contexts, token);
            using var admin = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
            using var reader = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });

            async Task<(IDocument Document, Dictionary<string, string> Fields)> Form(HttpClient client, string url, string formName)
            {
                using var response = await client.GetAsync(url, token);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token);
                var form = document.QuerySelectorAll("form").Single(element => element.QuerySelector("input[name='_handler']")?.GetAttribute("value") == formName);
                var fields = form.QuerySelectorAll("input[type=hidden][name]")
                    .ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
                return (document, fields);
            }
            async Task<HttpResponseMessage> Post(HttpClient client, string url, string name, params (string Key, string Value)[] values)
            {
                var (_, fields) = await Form(client, url, name);
                foreach (var (key, value) in values) fields[key] = value;
                return await client.PostAsync(url, new FormUrlEncodedContent(fields), token);
            }
            using (var setup = await Post(admin, "/account/setup", "setup", ("Input.SetupToken", setupSecret),
                ("Input.UserName", "admin"), ("Input.Password", "Strong-Password-123"), ("Input.ConfirmPassword", "Strong-Password-123")))
                Assert.AreEqual(HttpStatusCode.Redirect, setup.StatusCode);
            using (var login = await Post(admin, "/account/login", "login", ("Input.UserName", "admin"), ("Input.Password", "Strong-Password-123")))
                Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode);
            using var created = await Post(admin, "/account/manage", "create-account", ("Input.UserName", "reader"), ("Input.Role", "Read only"));
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode);
            var createdDocument = await new HtmlParser().ParseDocumentAsync(await created.Content.ReadAsStringAsync(token), token);
            var code = createdDocument.QuerySelector("textarea[aria-label='一次性設定密碼代碼']")?.TextContent;
            Assert.IsFalse(string.IsNullOrWhiteSpace(code));
            Assert.IsTrue(created.Headers.CacheControl?.NoStore);

            using (var reset = await Post(reader, "/account/reset-password", "reset-password", ("Input.UserName", "reader"),
                ("Input.Code", code!), ("Input.Password", "Reader-Password-123"), ("Input.ConfirmPassword", "Reader-Password-123")))
                Assert.AreEqual(HttpStatusCode.Redirect, reset.StatusCode);
            using (var login = await Post(reader, "/account/login", "login", ("Input.UserName", "reader"), ("Input.Password", "Reader-Password-123")))
                Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode);
            using (var anonymous = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") }))
            using (var denied = await anonymous.GetAsync("/api/v1/accounts/", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            using (var denied = await reader.GetAsync("/api/v1/accounts/", token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using (var noCsrf = await admin.PostAsJsonAsync("/api/v1/accounts/", new { userName = "api-reader", role = "Read only" }, token))
                Assert.AreEqual(HttpStatusCode.BadRequest, noCsrf.StatusCode);
            using (var anti = await admin.GetAsync("/api/v1/session/antiforgery", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, anti.StatusCode);
                using var json = JsonDocument.Parse(await anti.Content.ReadAsStringAsync(token));
                admin.DefaultRequestHeaders.Add("RequestVerificationToken", json.RootElement.GetProperty("requestToken").GetString());
            }
            Guid apiId;
            using (var apiCreated = await admin.PostAsJsonAsync("/api/v1/accounts/", new { userName = "api-reader", role = "Read only" }, token))
            {
                Assert.AreEqual(HttpStatusCode.Created, apiCreated.StatusCode);
                Assert.IsTrue(apiCreated.Headers.CacheControl?.NoStore);
                using var json = JsonDocument.Parse(await apiCreated.Content.ReadAsStringAsync(token));
                apiId = json.RootElement.GetProperty("id").GetGuid();
                Assert.IsFalse(string.IsNullOrEmpty(json.RootElement.GetProperty("setupCode").GetString()));
            }
            using (var apiGet = await admin.GetAsync($"/api/v1/accounts/{apiId}", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, apiGet.StatusCode);
                var body = await apiGet.Content.ReadAsStringAsync(token);
                Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("setupCode", body, StringComparison.OrdinalIgnoreCase);
            }
            using (var renamed = await admin.PutAsJsonAsync($"/api/v1/accounts/{apiId}/name", new { userName = "api-renamed" }, token)) Assert.AreEqual(HttpStatusCode.NoContent, renamed.StatusCode);
            using (var invalidRole = await admin.PutAsJsonAsync($"/api/v1/accounts/{apiId}/role", new { role = "Unknown" }, token)) Assert.AreEqual(HttpStatusCode.BadRequest, invalidRole.StatusCode);
            using (var role = await admin.PutAsJsonAsync($"/api/v1/accounts/{apiId}/role", new { role = "Administrator" }, token)) Assert.AreEqual(HttpStatusCode.NoContent, role.StatusCode);
            using (var reset = await admin.PostAsync($"/api/v1/accounts/{apiId}/password-reset", null, token)) Assert.AreEqual(HttpStatusCode.OK, reset.StatusCode);
            using (var deleted = await admin.DeleteAsync($"/api/v1/accounts/{apiId}", token)) Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode);
            using (var denied = await reader.GetAsync("/account/manage", token))
            {
                Assert.AreEqual(HttpStatusCode.Redirect, denied.StatusCode);
                Assert.Contains("/account/access-denied", denied.Headers.Location?.OriginalString ?? "");
            }
            using (var changed = await Post(reader, "/account/change-password", "change-password", ("Input.CurrentPassword", "Reader-Password-123"),
                ("Input.NewPassword", "Reader-New-Password-456"), ("Input.ConfirmPassword", "Reader-New-Password-456")))
                Assert.AreEqual(HttpStatusCode.Redirect, changed.StatusCode);
            using (var signedOut = await reader.GetAsync("/account/change-password", token)) Assert.AreEqual(HttpStatusCode.Redirect, signedOut.StatusCode);
            using (var newLogin = await Post(reader, "/account/login", "login", ("Input.UserName", "reader"), ("Input.Password", "Reader-New-Password-456")))
                Assert.AreEqual(HttpStatusCode.Redirect, newLogin.StatusCode);

            Guid readerId;
            await using (var db = await contexts.CreateDbContextAsync(token))
                readerId = (await db.Users.SingleAsync(user => user.NormalizedUserName == "READER", token)).Id;
            var detailUrl = $"/account/manage/{readerId}";
            using (var forbidden = await reader.GetAsync(detailUrl, token))
            {
                Assert.AreEqual(HttpStatusCode.Redirect, forbidden.StatusCode);
                Assert.Contains("/account/access-denied", forbidden.Headers.Location?.OriginalString ?? "");
            }
            using (var rename = await Post(admin, detailUrl, "rename-account", ("NameInput.UserName", "reader2")))
                Assert.AreEqual(HttpStatusCode.OK, rename.StatusCode);
            using (var afterRename = await reader.GetAsync("/account/change-password", token))
                Assert.AreEqual(HttpStatusCode.Redirect, afterRename.StatusCode);
            using (var renamedLogin = await Post(reader, "/account/login", "login", ("Input.UserName", "reader2"), ("Input.Password", "Reader-New-Password-456")))
                Assert.AreEqual(HttpStatusCode.Redirect, renamedLogin.StatusCode);
            using (var forged = await reader.PostAsync(detailUrl, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_handler"] = "change-role", ["RoleInput.Role"] = "Administrator"
            }), token))
            {
                Assert.AreEqual(HttpStatusCode.Redirect, forged.StatusCode);
                Assert.Contains("/account/access-denied", forged.Headers.Location?.OriginalString ?? "");
            }
            using (var promote = await Post(admin, detailUrl, "change-role", ("RoleInput.Role", "Administrator")))
                Assert.AreEqual(HttpStatusCode.OK, promote.StatusCode);
            using (var stale = await reader.GetAsync("/account/change-password", token))
            {
                Assert.AreEqual(HttpStatusCode.Redirect, stale.StatusCode);
                Assert.Contains("/account/login", stale.Headers.Location?.OriginalString ?? "");
            }
            using (var demote = await Post(admin, detailUrl, "change-role", ("RoleInput.Role", "Read only")))
                Assert.AreEqual(HttpStatusCode.OK, demote.StatusCode);
            using var resetIssued = await Post(admin, detailUrl, "reset-code");
            Assert.AreEqual(HttpStatusCode.OK, resetIssued.StatusCode);
            var resetDocument = await new HtmlParser().ParseDocumentAsync(await resetIssued.Content.ReadAsStringAsync(token), token);
            var resetCode = resetDocument.QuerySelector("textarea[aria-label='一次性設定密碼代碼']")?.TextContent;
            Assert.IsFalse(string.IsNullOrWhiteSpace(resetCode));
            using (var resetAgain = await Post(reader, "/account/reset-password", "reset-password", ("Input.UserName", "reader2"),
                ("Input.Code", resetCode!), ("Input.Password", "Reader-Reset-Password-789"), ("Input.ConfirmPassword", "Reader-Reset-Password-789")))
                Assert.AreEqual(HttpStatusCode.Redirect, resetAgain.StatusCode);
            using (var login = await Post(reader, "/account/login", "login", ("Input.UserName", "reader2"), ("Input.Password", "Reader-Reset-Password-789")))
                Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode);
            using (var anti = await reader.GetAsync("/api/v1/session/antiforgery", token))
            {
                using var json = JsonDocument.Parse(await anti.Content.ReadAsStringAsync(token));
                reader.DefaultRequestHeaders.Add("RequestVerificationToken", json.RootElement.GetProperty("requestToken").GetString());
            }
            using (var changed = await reader.PutAsJsonAsync("/api/v1/session/password", new { currentPassword = "Reader-Reset-Password-789", newPassword = "Reader-Final-Password-123" }, token))
                Assert.AreEqual(HttpStatusCode.NoContent, changed.StatusCode);
            using (var stale = await reader.GetAsync("/api/v1/session/antiforgery", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, stale.StatusCode);
            using (var deletion = await Post(admin, detailUrl, "delete-account", ("DeleteInput.Confirmed", "true")))
                Assert.AreEqual(HttpStatusCode.Redirect, deletion.StatusCode);
            await using (var db = await contexts.CreateDbContextAsync(token))
                Assert.IsFalse(await db.Users.AnyAsync(user => user.Id == readerId, token));
        }, TestContext.CancellationToken);
}
