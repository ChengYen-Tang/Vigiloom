using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class ApiCredentialHttpTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task CredentialLifecycle_IsAuthorizedAuditedAndImmediatelyRevoked() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
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
            await RoleInitializer.InitializeAsync(contexts, token);
            var settings = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };
            using var admin = factory.CreateClient(settings);
            using var client = factory.CreateClient(settings);
            async Task<(IDocument Document, Dictionary<string, string> Fields)> Form(string url, string name)
            {
                using var response = await admin.GetAsync(url, token);
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var doc = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token);
                var form = doc.QuerySelectorAll("form").Single(e => e.QuerySelector("input[name='_handler']")?.GetAttribute("value") == name);
                return (doc, form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(e => e.GetAttribute("name")!, e => e.GetAttribute("value") ?? ""));
            }
            async Task<HttpResponseMessage> Post(string url, string name, params (string Key, string Value)[] values)
            {
                var (_, fields) = await Form(url, name);
                foreach (var (key, value) in values) fields[key] = value;
                return await admin.PostAsync(url, new FormUrlEncodedContent(fields), token);
            }
            using (var denied = await client.GetAsync("/account/api-credentials", token)) Assert.AreEqual(HttpStatusCode.Redirect, denied.StatusCode);
            using (var anonymousApi = await client.GetAsync("/api/v1/access", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousApi.StatusCode);
            using (var setup = await Post("/account/setup", "setup", ("Input.SetupToken", setupSecret), ("Input.UserName", "admin"),
                ("Input.Password", "Strong-Password-123"), ("Input.ConfirmPassword", "Strong-Password-123"))) Assert.AreEqual(HttpStatusCode.Redirect, setup.StatusCode);
            using (var login = await Post("/account/login", "login", ("Input.UserName", "admin"), ("Input.Password", "Strong-Password-123"))) Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode);
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var manager = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
                var readerAccount = new Account { UserName = "reader" };
                Assert.IsTrue((await manager.CreateAsync(readerAccount, "Reader-Password-123")).Succeeded);
                Assert.IsTrue((await manager.AddToRoleAsync(readerAccount, BuiltInRoles.ReadOnly)).Succeeded);
            }
            using (var reader = factory.CreateClient(settings))
            {
                using var loginPage = await reader.GetAsync("/account/login", token);
                var doc = await new HtmlParser().ParseDocumentAsync(await loginPage.Content.ReadAsStringAsync(token), token);
                var form = doc.QuerySelectorAll("form").Single(e => e.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "login");
                var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(e => e.GetAttribute("name")!, e => e.GetAttribute("value") ?? "");
                fields["Input.UserName"] = "reader"; fields["Input.Password"] = "Reader-Password-123";
                using var loggedIn = await reader.PostAsync("/account/login", new FormUrlEncodedContent(fields), token);
                Assert.AreEqual(HttpStatusCode.Redirect, loggedIn.StatusCode);
                using var denied = await reader.GetAsync("/account/api-credentials", token);
                Assert.AreEqual(HttpStatusCode.Redirect, denied.StatusCode);
                Assert.Contains("/account/access-denied", denied.Headers.Location?.OriginalString ?? "");
            }
            using (var forged = await admin.PostAsync("/account/api-credentials", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["_handler"] = "issue-api-credential", ["IssueInput.Name"] = "forged", ["IssueInput.Days"] = "90", ["IssueInput.Password"] = "Strong-Password-123"
            }), token)) Assert.AreEqual(HttpStatusCode.BadRequest, forged.StatusCode);
            using (var wrongPassword = await Post("/account/api-credentials", "issue-api-credential", ("IssueInput.Name", "rejected"),
                ("IssueInput.Days", "90"), ("IssueInput.Password", "wrong")))
            {
                Assert.AreEqual(HttpStatusCode.OK, wrongPassword.StatusCode);
                Assert.DoesNotContain("一次性 API 憑證", await wrongPassword.Content.ReadAsStringAsync(token));
            }
            async Task<string> Issue(string name, bool canWrite)
            {
                using var response = await Post("/account/api-credentials", "issue-api-credential", ("IssueInput.Name", name),
                    ("IssueInput.Days", "90"), ("IssueInput.CanWrite", canWrite ? "true" : "false"), ("IssueInput.Password", "Strong-Password-123"));
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.IsTrue(response.Headers.CacheControl?.NoStore);
                var doc = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token);
                var secret = doc.QuerySelector("textarea[aria-label='一次性 API 憑證']")?.TextContent;
                Assert.IsFalse(string.IsNullOrEmpty(secret));
                return secret!;
            }
            var first = await Issue("integration reader", false);
            var second = await Issue("integration writer", true);
            using (var list = await admin.GetAsync("/account/api-credentials", token))
            {
                var html = await list.Content.ReadAsStringAsync(token);
                Assert.DoesNotContain(first, html);
                Assert.DoesNotContain(second, html);
                Assert.Contains("integration reader", html);
            }
            var id = Guid.ParseExact(first.Substring(4, 36), "D");
            Assert.AreEqual('7', id.ToString("D")[14]);
            var secondId = Guid.ParseExact(second.Substring(4, 36), "D");
            await using (var db = await contexts.CreateDbContextAsync(token))
            {
                var record = await db.ApiCredentials.SingleAsync(x => x.Id == id, token);
                CollectionAssert.AreEqual(SHA256.HashData(WebEncoders.Base64UrlDecode(first[41..])), record.Verifier);
                Assert.AreEqual(2, await db.ApiCredentialAuditEvents.CountAsync(token));
                await db.Database.OpenConnectionAsync(token);
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = """
                    SELECT COUNT(*) FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id
                    JOIN sys.schemas s ON s.schema_id = t.schema_id
                    WHERE s.name = 'identity' AND t.name = 'ApiCredentials' AND
                    ((i.name = 'PK_ApiCredentials' AND i.is_primary_key = 1 AND i.type = 2) OR
                     (i.name = 'IX_ApiCredentials_RowId' AND i.is_unique = 1 AND i.type = 1))
                    """;
                Assert.AreEqual(2, (int)(await command.ExecuteScalarAsync(token))!);
            }
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first);
            using (var valid = await client.GetAsync("/api/v1/access", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, valid.StatusCode);
                Assert.IsTrue(valid.Headers.CacheControl?.NoStore);
                using var json = JsonDocument.Parse(await valid.Content.ReadAsStringAsync(token));
                Assert.AreEqual(id.ToString("D"), json.RootElement.GetProperty("clientId").GetString());
                Assert.HasCount(1, json.RootElement.GetProperty("scopes").EnumerateArray());
            }
            using (var cannotManageAccounts = await client.GetAsync("/api/v1/accounts/", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, cannotManageAccounts.StatusCode);
            using (var cannotViewPlatform = await client.GetAsync("/api/v1/operations/platform-status", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, cannotViewPlatform.StatusCode);
            foreach (var source in new[] { "accounts", "api-credentials", "devices", "agent-identity", "agent-settings", "scheduling" })
                using (var cannotViewAudit = await client.GetAsync($"/api/v1/operations/audit-feed/{source}?from=2026-11-01T00%3A00%3A00Z&to=2026-11-02T00%3A00%3A00Z", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, cannotViewAudit.StatusCode);
            var changed = first.ToCharArray(); changed[41] = changed[41] == 'A' ? 'B' : 'A';
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string(changed));
            using (var invalid = await client.GetAsync("/api/v1/access", token))
            {
                Assert.AreEqual(HttpStatusCode.Unauthorized, invalid.StatusCode);
                using var json = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync(token));
                Assert.AreEqual("unauthorized", json.RootElement.GetProperty("code").GetString());
            }
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second);
            using (var valid = await client.GetAsync("/api/v1/access", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, valid.StatusCode);
                using var json = JsonDocument.Parse(await valid.Content.ReadAsStringAsync(token));
                Assert.HasCount(2, json.RootElement.GetProperty("scopes").EnumerateArray());
            }
            using (var revoke = await Post("/account/api-credentials", "revoke-api-credential", ("RevokeInput.Id", id.ToString("D")),
                ("RevokeInput.Password", "Strong-Password-123"), ("RevokeInput.Confirmed", "true"))) Assert.AreEqual(HttpStatusCode.OK, revoke.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", first);
            using (var revoked = await client.GetAsync("/api/v1/access", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", second);
            using (var stillValid = await client.GetAsync("/api/v1/access", token)) Assert.AreEqual(HttpStatusCode.OK, stillValid.StatusCode);
            await using (var db = await contexts.CreateDbContextAsync(token))
            {
                var record = await db.ApiCredentials.SingleAsync(x => x.Id == secondId, token);
                db.Entry(record).Property(x => x.ExpiresAt).CurrentValue = DateTimeOffset.UtcNow.AddMinutes(-1);
                await db.SaveChangesAsync(token);
                Assert.AreEqual(3, await db.ApiCredentialAuditEvents.CountAsync(token));
            }
            using (var expired = await client.GetAsync("/api/v1/access", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, expired.StatusCode);
        }, TestContext.CancellationToken);
}
