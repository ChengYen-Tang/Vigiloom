using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Api;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AgentIdentityHttpTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task AdministratorEnrollmentAndMachineIdentityAreIsolated() =>
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
            var identities = factory.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
            await using (var db = await identities.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var resources = factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
            await using (var db = await resources.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await RoleInitializer.InitializeAsync(identities, token);
            await using (var scope = factory.Services.CreateAsyncScope())
                Assert.AreEqual(InitialAdministratorStatus.Created, (await scope.ServiceProvider.GetRequiredService<InitialAdministrator>()
                    .CreateAsync(setupSecret, "admin", "Strong-Password-123", token)).Status);
            var device = new Device("test server", "127.0.0.1", true, DateTimeOffset.UtcNow);
            await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }

            var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };
            using var admin = factory.CreateClient(options);
            using var agent = factory.CreateClient(options);
            using (var unauthenticated = await agent.PostAsync($"/api/v1/agent-management/devices/{device.Id:D}/enrollments", null, token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
            using (var loginPage = await admin.GetAsync("/account/login", token))
            {
                var doc = await new HtmlParser().ParseDocumentAsync(await loginPage.Content.ReadAsStringAsync(token), token);
                var form = doc.QuerySelectorAll("form").Single(e => e.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "login");
                var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(e => e.GetAttribute("name")!, e => e.GetAttribute("value") ?? "");
                fields["Input.UserName"] = "admin"; fields["Input.Password"] = "Strong-Password-123";
                using var login = await admin.PostAsync("/account/login", new FormUrlEncodedContent(fields), token);
                Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode);
            }
            using (var forged = await admin.PostAsync($"/api/v1/agent-management/devices/{device.Id:D}/enrollments", null, token))
                Assert.AreEqual(HttpStatusCode.BadRequest, forged.StatusCode);
            using (var cookieCannotActAsAgent = await admin.GetAsync("/api/v1/agent/identity", token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, cookieCannotActAsAgent.StatusCode);
            using (var page = await admin.GetAsync($"/devices/{device.Id:D}/agent", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
                var doc = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token);
                Assert.Contains("管理 Agent 身分", doc.Body!.TextContent);
                var issueForm = doc.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "agent-issue");
                var fields = issueForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(e => e.GetAttribute("name")!, e => e.GetAttribute("value") ?? "");
                fields["Issue.Confirm"] = "true";
                using var posted = await admin.PostAsync($"/devices/{device.Id:D}/agent", new FormUrlEncodedContent(fields), token);
                var postedDoc = await new HtmlParser().ParseDocumentAsync(await posted.Content.ReadAsStringAsync(token), token);
                Assert.IsNotNull(postedDoc.QuerySelector("textarea[aria-label='Agent 加入資料']"));
                var revokeForm = postedDoc.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "enrollment-revoke");
                var revokeFields = revokeForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(e => e.GetAttribute("name")!, e => e.GetAttribute("value") ?? "");
                revokeFields["RevokeEnrollment.Confirm"] = "true";
                revokeFields["RevokeEnrollment.Id"] = revokeForm.QuerySelector("button[name='RevokeEnrollment.Id']")!.GetAttribute("value")!;
                using var uiRevoke = await admin.PostAsync($"/devices/{device.Id:D}/agent", new FormUrlEncodedContent(revokeFields), token);
                Assert.Contains("已撤銷", (await new HtmlParser().ParseDocumentAsync(await uiRevoke.Content.ReadAsStringAsync(token), token)).Body!.TextContent);
            }
            using (var anti = await admin.GetAsync("/api/v1/session/antiforgery", token))
            {
                using var json = JsonDocument.Parse(await anti.Content.ReadAsStringAsync(token));
                admin.DefaultRequestHeaders.Add("RequestVerificationToken", json.RootElement.GetProperty("requestToken").GetString());
            }
            using var issued = await admin.PostAsync($"/api/v1/agent-management/devices/{device.Id:D}/enrollments", null, token);
            Assert.AreEqual(HttpStatusCode.OK, issued.StatusCode);
            Assert.IsTrue(issued.Headers.CacheControl?.NoStore);
            var enrollment = await issued.Content.ReadFromJsonAsync<EnrollmentSecretResponse>(cancellationToken: token);
            Assert.IsNotNull(enrollment);
            var secret = RandomNumberGenerator.GetBytes(32);
            var request = new EnrollmentRequest(enrollment.Secret, SHA256.HashData(secret));
            using var response = await agent.PostAsJsonAsync("/api/v1/agent/enroll", request, token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var result = await response.Content.ReadFromJsonAsync<EnrollmentResponse>(cancellationToken: token);
            Assert.IsNotNull(result);
            agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", enrollment.Secret);
            using (var oneTimeNotMachine = await agent.GetAsync("/api/v1/agent/identity", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, oneTimeNotMachine.StatusCode);
            var machineSecret = Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"vla_{result.AgentId:D}.{machineSecret}");
            using (var valid = await agent.GetAsync("/api/v1/agent/identity", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, valid.StatusCode);
                Assert.AreEqual(device.Id, (await valid.Content.ReadFromJsonAsync<AgentIdentityResponse>(cancellationToken: token))!.DeviceId);
            }
            using (var api = await agent.GetAsync("/api/v1/access", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, api.StatusCode);
            using (var management = await agent.PostAsync($"/api/v1/agent-management/agents/{result.AgentId:D}/revoke", null, token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, management.StatusCode);
            var nextSecret = RandomNumberGenerator.GetBytes(32);
            var nextCredential = $"vla_{result.AgentId:D}.{Convert.ToBase64String(nextSecret).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
            using (var startRotation = await agent.PostAsJsonAsync("/api/v1/agent/rotation/start", new RotationRequest(SHA256.HashData(nextSecret)), token))
                Assert.AreEqual(HttpStatusCode.NoContent, startRotation.StatusCode);
            using (var oldCannotFinish = await agent.PostAsync("/api/v1/agent/rotation/finish", null, token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, oldCannotFinish.StatusCode);
            agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", nextCredential);
            using (var pending = await agent.GetAsync("/api/v1/agent/identity", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, pending.StatusCode);
            using (var finish = await agent.PostAsync("/api/v1/agent/rotation/finish", null, token)) Assert.AreEqual(HttpStatusCode.NoContent, finish.StatusCode);
            using (var retry = await agent.PostAsync("/api/v1/agent/rotation/finish", null, token)) Assert.AreEqual(HttpStatusCode.NoContent, retry.StatusCode);
            using (var rotated = await agent.GetAsync("/api/v1/agent/identity", token)) Assert.AreEqual(HttpStatusCode.OK, rotated.StatusCode);
            using (var revoke = await admin.PostAsync($"/api/v1/agent-management/agents/{result.AgentId:D}/revoke", null, token))
                Assert.AreEqual(HttpStatusCode.NoContent, revoke.StatusCode);
            using (var revoked = await agent.GetAsync("/api/v1/agent/identity", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);
        }, TestContext.CancellationToken);
}
