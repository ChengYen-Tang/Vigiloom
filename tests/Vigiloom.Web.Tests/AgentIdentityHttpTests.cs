using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Contracts.Devices;
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
    public Task AgentRecoversAfterCommittedEnrollmentResponseIsLostAndExpires() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var clock = new TestClock();
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Vigiloom"] = database.ConnectionString
                }));
                builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock));
            });
            var resources = factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
            await using (var db = await resources.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var agents = factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>();
            await using (var db = await agents.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var device = new Device("test server", "127.0.0.1", true, clock.Now);
            await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
            EnrollmentIssue issued;
            await using (var scope = factory.Services.CreateAsyncScope())
                issued = await scope.ServiceProvider.GetRequiredService<AgentIdentity>().IssueAsync(device.Id, Guid.CreateVersion7(), token);
            Assert.AreEqual(AgentIdentityStatus.Success, issued.Status);
            var root = Path.Combine(Path.GetTempPath(), "VigiloomAgentHttpTests", Path.GetRandomFileName());
            try
            {
                var vault = new IdentityVault(root);
                using var client = new HttpClient(new DropFirstEnrollmentResponse(factory.Server.CreateHandler())) { MaxResponseContentBufferSize = 65536 };
                var coordinator = new IdentityClient(client);
                var server = new Uri("https://localhost/");
                await Assert.ThrowsAsync<HttpRequestException>(() => coordinator.EnrollAsync(vault, server, issued.Secret, token));
                await using (var db = await agents.CreateDbContextAsync(token)) Assert.AreEqual(1, await db.Agents.CountAsync(token));
                clock.Now = clock.Now.AddMinutes(16);
                var recoveredId = await coordinator.EnrollAsync(vault, server, null, token);
                await using (var session = vault.Acquire())
                {
                    var state = await session.ReadAsync(token);
                    Assert.IsNotNull(state); Assert.AreEqual(recoveredId, state.AgentId); Assert.IsNull(state.EnrollmentSecret);
                }
                Assert.AreEqual(recoveredId, await coordinator.EnrollAsync(vault, server, null, token));
                await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.EnrollAsync(vault, new Uri("https://other.example/"), null, token));
                using (var rotationHttp = new HttpClient(new DropFirstRotationResponse(factory.Server.CreateHandler())))
                {
                    var rotation = new IdentityClient(rotationHttp);
                    await Assert.ThrowsAsync<HttpRequestException>(() => rotation.RotateAsync(vault, server, token));
                }
                await using (var session = vault.Acquire())
                {
                    var pendingState = await session.ReadAsync(token);
                    Assert.IsNotNull(pendingState?.PendingSecret, "A lost confirmation must retain both machine secrets.");
                }
                using (var retryHttp = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = server, AllowAutoRedirect = false }))
                {
                    await new IdentityClient(retryHttp).RotateAsync(vault, server, token);
                    await using var session = vault.Acquire();
                    var rotatedState = await session.ReadAsync(token);
                    Assert.IsNotNull(rotatedState); Assert.IsNull(rotatedState.PendingSecret);
                    retryHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"vla_{recoveredId:D}.{rotatedState.MachineSecret}");
                    using var authenticated = await retryHttp.GetAsync("/api/v1/agent/identity", token);
                    Assert.AreEqual(HttpStatusCode.OK, authenticated.StatusCode);
                }
                await using (var db = await agents.CreateDbContextAsync(token)) Assert.AreEqual(1, await db.Agents.CountAsync(token));
            }
            finally
            {
                var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "VigiloomAgentHttpTests")) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(root).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("Test cleanup path escaped its designated temporary root.");
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }, TestContext.CancellationToken);

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
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await RoleInitializer.InitializeAsync(identities, token);
            string apiCredential;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                Assert.AreEqual(InitialAdministratorStatus.Created, (await scope.ServiceProvider.GetRequiredService<InitialAdministrator>()
                    .CreateAsync(setupSecret, "admin", "Strong-Password-123", token)).Status);
                var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
                var reader = new Account { UserName = "reader" };
                Assert.IsTrue((await users.CreateAsync(reader, "Strong-Password-123")).Succeeded);
                Assert.IsTrue((await users.AddToRoleAsync(reader, BuiltInRoles.ReadOnly)).Succeeded);
                var adminAccount = await users.FindByNameAsync("admin");
                Assert.IsNotNull(adminAccount);
                var bytes = RandomNumberGenerator.GetBytes(32);
                var credential = new ApiCredential("test", SHA256.HashData(bytes), true, DateTimeOffset.UtcNow.AddDays(1), adminAccount.Id);
                var db = scope.ServiceProvider.GetRequiredService<IdentityAccessDbContext>();
                db.ApiCredentials.Add(credential); await db.SaveChangesAsync(token);
                apiCredential = $"vlg_{credential.Id:D}.{WebEncoders.Base64UrlEncode(bytes)}";
            }
            var device = new Device("test server", "127.0.0.1", true, DateTimeOffset.UtcNow);
            await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }

            var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };
            using var admin = factory.CreateClient(options);
            using var agent = factory.CreateClient(options);
            using var api = factory.CreateClient(options);
            api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiCredential);
            using (var anonymousStatus = await agent.GetAsync($"/api/v1/devices/{device.Id:D}/agent-status", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousStatus.StatusCode);
            using (var apiStatus = await api.GetAsync($"/api/v1/devices/{device.Id:D}/agent-status", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, apiStatus.StatusCode);
                Assert.IsTrue(apiStatus.Headers.CacheControl?.NoStore);
                Assert.AreEqual(AgentConnectionState.NotRegistered, (await apiStatus.Content.ReadFromJsonAsync<AgentConnectionStatus>(cancellationToken: token))!.State);
            }
            using (var apiCannotImpersonate = await api.GetAsync("/api/v1/agent/identity", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, apiCannotImpersonate.StatusCode);
            using (var apiCannotHeartbeat = await api.PostAsync("/api/v1/agent/heartbeat", null, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, apiCannotHeartbeat.StatusCode);
            using (var anonymousCannotHeartbeat = await agent.PostAsync("/api/v1/agent/heartbeat", null, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousCannotHeartbeat.StatusCode);
            using (var apiCannotManage = await api.PostAsync($"/api/v1/agent-management/devices/{device.Id:D}/enrollments", null, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, apiCannotManage.StatusCode);
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
            using (var reader = factory.CreateClient(options))
            {
                using var page = await reader.GetAsync("/account/login", token);
                var doc = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token);
                var form = doc.QuerySelectorAll("form").Single(e => e.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "login");
                var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(e => e.GetAttribute("name")!, e => e.GetAttribute("value") ?? "");
                fields["Input.UserName"] = "reader"; fields["Input.Password"] = "Strong-Password-123";
                using var login = await reader.PostAsync("/account/login", new FormUrlEncodedContent(fields), token);
                Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode);
                using var status = await reader.GetAsync($"/api/v1/devices/{device.Id:D}/agent-status", token);
                Assert.AreEqual(HttpStatusCode.OK, status.StatusCode);
                Assert.AreEqual(AgentConnectionState.NotRegistered, (await status.Content.ReadFromJsonAsync<AgentConnectionStatus>(cancellationToken: token))!.State);
                using var details = await reader.GetAsync($"/devices/{device.Id:D}", token);
                var detailsDoc = await new HtmlParser().ParseDocumentAsync(await details.Content.ReadAsStringAsync(token), token);
                Assert.Contains("Agent 狀態", detailsDoc.Body!.TextContent);
                Assert.Contains("未註冊或已撤銷", detailsDoc.Body.TextContent);
                Assert.IsNull(detailsDoc.QuerySelector($"a[href='/devices/{device.Id:D}/agent']"));
                using var denied = await reader.PostAsync($"/api/v1/agent-management/devices/{device.Id:D}/enrollments", null, token);
                Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            }
            using (var forged = await admin.PostAsync($"/api/v1/agent-management/devices/{device.Id:D}/enrollments", null, token))
                Assert.AreEqual(HttpStatusCode.BadRequest, forged.StatusCode);
            using (var cookieCannotActAsAgent = await admin.GetAsync("/api/v1/agent/identity", token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, cookieCannotActAsAgent.StatusCode);
            using (var cookieCannotHeartbeat = await admin.PostAsync("/api/v1/agent/heartbeat", null, token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, cookieCannotHeartbeat.StatusCode);
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
            var proofSecret = Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"vlr_{enrollment.Secret.Substring(4, 36)}.{proofSecret}");
            using (var recovery = await agent.PostAsJsonAsync("/api/v1/agent/recover", new EnrollmentRecoveryRequest(enrollment.Secret), token))
            {
                Assert.AreEqual(HttpStatusCode.OK, recovery.StatusCode);
                Assert.AreEqual(result.AgentId, (await recovery.Content.ReadFromJsonAsync<EnrollmentResponse>(cancellationToken: token))!.AgentId);
            }
            agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", enrollment.Secret);
            using (var oneTimeNotMachine = await agent.GetAsync("/api/v1/agent/identity", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, oneTimeNotMachine.StatusCode);
            var machineSecret = Convert.ToBase64String(secret).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"vla_{result.AgentId:D}.{machineSecret}");
            using (var valid = await agent.GetAsync("/api/v1/agent/identity", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, valid.StatusCode);
                Assert.AreEqual(device.Id, (await valid.Content.ReadFromJsonAsync<AgentIdentityResponse>(cancellationToken: token))!.DeviceId);
            }
            using (var heartbeat = await agent.PostAsync("/api/v1/agent/heartbeat", null, token)) Assert.AreEqual(HttpStatusCode.NoContent, heartbeat.StatusCode);
            using (var machineStatus = await agent.GetAsync($"/api/v1/devices/{device.Id:D}/agent-status", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, machineStatus.StatusCode);
            using (var connected = await api.GetAsync($"/api/v1/devices/{device.Id:D}/agent-status", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, connected.StatusCode);
                var status = await connected.Content.ReadFromJsonAsync<AgentConnectionStatus>(cancellationToken: token);
                Assert.AreEqual(AgentConnectionState.Online, status!.State);
                Assert.IsNotNull(status.LastHeartbeatAt);
            }
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
                Assert.IsNotNull((await db.Agents.SingleAsync(x => x.Id == result.AgentId, token)).LastHeartbeatAt);
            using (var apiAccess = await agent.GetAsync("/api/v1/access", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, apiAccess.StatusCode);
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
            agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"vla_{result.AgentId:D}.{machineSecret}");
            using (var oldHeartbeat = await agent.PostAsync("/api/v1/agent/heartbeat", null, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, oldHeartbeat.StatusCode);
            agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", nextCredential);
            using (var rotatedHeartbeat = await agent.PostAsync("/api/v1/agent/heartbeat", null, token)) Assert.AreEqual(HttpStatusCode.NoContent, rotatedHeartbeat.StatusCode);
            using (var revoke = await admin.PostAsync($"/api/v1/agent-management/agents/{result.AgentId:D}/revoke", null, token))
                Assert.AreEqual(HttpStatusCode.NoContent, revoke.StatusCode);
            using (var revoked = await agent.GetAsync("/api/v1/agent/identity", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);
            using (var revokedHeartbeat = await agent.PostAsync("/api/v1/agent/heartbeat", null, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, revokedHeartbeat.StatusCode);
        }, TestContext.CancellationToken);

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class DropFirstEnrollmentResponse(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private bool dropped;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (!dropped && request.RequestUri?.AbsolutePath == "/api/v1/agent/enroll")
            {
                dropped = true;
                response.Dispose();
                throw new HttpRequestException("Simulated connection loss after the server committed.");
            }
            return response;
        }
    }

    private sealed class DropFirstRotationResponse(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (request.RequestUri?.AbsolutePath == "/api/v1/agent/rotation/finish" && response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new HttpRequestException("Simulated connection loss after rotation committed.");
            }
            return response;
        }
    }
}
