using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Identity;
using Vigiloom.Contracts.Resources;
using Vigiloom.Contracts.Workspace;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.UpdateSecurity.AgentPackages;

namespace Vigiloom.Modules.Resources.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class InternalApiTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ReturnsEnabledTargetsAndCurrentAgentEligibility()
    {
        var temporal = Environment.GetEnvironmentVariable("VIGILOOM_TEST_TEMPORAL");
        if (string.IsNullOrWhiteSpace(temporal)) Assert.Inconclusive("Set VIGILOOM_TEST_TEMPORAL for the Temporal integration test.");
        var suffix = Guid.CreateVersion7().ToString("N");
        var networkQueue = "vigiloom-test-network-" + suffix;
        var agentQueue = "vigiloom-test-agent-" + suffix;
        var auditQueue = "vigiloom-test-audit-" + suffix;
        var observabilityQueue = "vigiloom-test-observation-" + suffix;
        var packagePath = Path.Combine(AppContext.BaseDirectory, "vigiloom-resource-flow-" + suffix);
        var packageBytes = Encoding.UTF8.GetBytes("agent package");
        var archiveHash = Convert.ToHexStringLower(SHA256.HashData(packageBytes));
        using var github = new HttpClient(new ReleaseHandler(packageBytes, archiveHash)) { BaseAddress = new Uri("https://api.github.com/") };
        var identityHandler = new TestIdentityHandler();
        try
        {
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("ConnectionStrings:Resources", database.ConnectionString);
                builder.UseSetting("Temporal:Address", temporal);
                builder.UseSetting("Work:NetworkQueue", networkQueue);
                builder.UseSetting("Work:AgentQueue", agentQueue);
                builder.UseSetting("Work:AuditQueue", auditQueue);
                builder.UseSetting("Work:ObservabilityQueue", observabilityQueue);
                builder.UseSetting("Vigiloom:AgentPackages:CachePath", packagePath);
                builder.ConfigureTestServices(services =>
                {
                    var dispatcher = services.SingleOrDefault(item => item.ImplementationType?.Name == "WorkDispatcher");
                    if (dispatcher is not null) services.Remove(dispatcher);
                    services.RemoveAll<GitHubAgentReleases>();
                    services.AddSingleton(new GitHubAgentReleases(github));
                    services.RemoveAll<IApiCredentialValidation>();
                    services.AddSingleton<IApiCredentialValidation, TestCredentialValidation>();
                    services.AddHttpClient(nameof(IdentityCookieValidator)).ConfigurePrimaryHttpMessageHandler(() => identityHandler);
                });
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ConnectionStrings:Resources"] = database.ConnectionString, ["Temporal:Address"] = temporal,
                        ["Work:NetworkQueue"] = networkQueue, ["Work:AgentQueue"] = agentQueue }));
            });
            var schedulingOptions = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var scheduling = new SchedulingDbContext(schedulingOptions)) await scheduling.Database.MigrateAsync(token);
            var contexts = host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
            var now = DateTimeOffset.UtcNow;
            var active = new Device("Active", "active.example", true, now);
            var disabled = new Device("Disabled", "disabled.example", true, now);
            var group = new Group("Servers", now);
            var serviceId = Guid.CreateVersion7();
            disabled.Update("Disabled", "disabled.example", true, false, now);
            await using (var db = await contexts.CreateDbContextAsync(token))
            {
                await db.Database.MigrateAsync(token);
                db.Devices.AddRange(active, disabled);
                db.Groups.Add(group);
                db.DeviceGroups.Add(new DeviceGroup(group.Id, active.Id));
                db.ServiceGroups.Add(new ServiceGroup(group.Id, serviceId));
                db.DeviceTargets.Add(new DeviceTarget(active.Id, "extra.example"));
                await db.SaveChangesAsync(token);
            }

            using var client = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            var packages = await client.GetFromJsonAsync<AgentPackagePage>("/internal/v1/agent-packages", token);
            Assert.IsNotNull(packages);
            Assert.IsEmpty(packages.Items);
            using (var badPage = await client.GetAsync("/internal/v1/agent-packages?page=0", token))
                Assert.AreEqual(HttpStatusCode.BadRequest, badPage.StatusCode);
            var targets = await client.GetFromJsonAsync<PingDeviceSnapshot[]>("/internal/v1/ping-targets", token);
            Assert.IsNotNull(targets);
            Assert.HasCount(1, targets);
            Assert.AreEqual(active.Id, targets[0].Id);
            CollectionAssert.AreEquivalent(new[] { "active.example", "extra.example" }, targets[0].Targets.ToArray());
            var activeAgent = await client.GetFromJsonAsync<DeviceAgentState>($"/internal/v1/devices/{active.Id:D}/agent", token);
            var disabledAgent = await client.GetFromJsonAsync<DeviceAgentState>($"/internal/v1/devices/{disabled.Id:D}/agent", token);
            Assert.IsTrue(activeAgent?.Eligible);
            Assert.IsFalse(disabledAgent?.Eligible);
            using var missing = await client.GetAsync($"/internal/v1/devices/{Guid.CreateVersion7():D}/agent", token);
            Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
            var members = await client.GetFromJsonAsync<GroupMembers>($"/internal/v1/groups/{group.Id:D}/members", token);
            Assert.IsNotNull(members);
            CollectionAssert.AreEquivalent(new[] { active.Id }, members.DeviceIds.ToArray());
            CollectionAssert.AreEquivalent(new[] { serviceId }, members.ServiceIds.ToArray());
            using var missingGroup = await client.GetAsync($"/internal/v1/groups/{Guid.CreateVersion7():D}/members", token);
            Assert.AreEqual(HttpStatusCode.NotFound, missingGroup.StatusCode);

            var actorId = Guid.CreateVersion7();
            using var issued = await client.PostAsJsonAsync($"/internal/v1/devices/{active.Id:D}/agent-enrollments", new AgentEnrollmentCommand(actorId), token);
            var enrollment = await issued.Content.ReadFromJsonAsync<AgentEnrollmentIssue>(cancellationToken: token);
            Assert.AreEqual(AgentRegistrationStatus.Success, enrollment?.Status);
            var machineSecret = RandomNumberGenerator.GetBytes(32);
            using var redeemed = await client.PostAsJsonAsync("/internal/v1/agent-enrollments/redeem", new AgentRedeemCommand(enrollment!.Secret, SHA256.HashData(machineSecret)), token);
            var registration = await redeemed.Content.ReadFromJsonAsync<AgentEnrollmentResult>(cancellationToken: token);
            Assert.AreEqual(AgentRegistrationStatus.Success, registration?.Status);
            var agentId = registration!.AgentId!.Value;
            var credential = AgentCredentials.Encode("vla", agentId, machineSecret);
            using var authenticated = await client.PostAsJsonAsync("/internal/v1/agents/authenticate", new AgentCredentialCommand(credential), token);
            Assert.AreEqual(agentId, (await authenticated.Content.ReadFromJsonAsync<RegisteredAgentIdentity>(cancellationToken: token))?.AgentId);
            using var downloaded = await client.PostAsJsonAsync("/internal/v1/agent-packages/download", new { Version = "1.2.3", RuntimeIdentifier = "win-x64" }, token);
            Assert.AreEqual(HttpStatusCode.NoContent, downloaded.StatusCode);
            var package = (await client.GetFromJsonAsync<AgentPackagePage>("/internal/v1/agent-packages", token))!.Items.Single();
            using var assigned = await client.PutAsJsonAsync($"/internal/v1/agents/{agentId:D}/assignment", new AgentVersionAssignmentCommand(package.Id, "", actorId), token);
            Assert.AreEqual(VersionAssignmentResult.Saved, await assigned.Content.ReadFromJsonAsync<VersionAssignmentResult>(cancellationToken: token));
            var target = await client.GetFromJsonAsync<AgentVersionTarget>($"/internal/v1/agents/{agentId:D}/target", token);
            Assert.IsNotNull(target);
            using var manifest = await client.GetAsync($"/internal/v1/agents/{agentId:D}/update/targets/{target.ManifestSha256}.json", token);
            Assert.AreEqual(HttpStatusCode.OK, manifest.StatusCode);
            CollectionAssert.AreEqual(packageBytes, await client.GetByteArrayAsync($"/internal/v1/agents/{agentId:D}/update/packages/{archiveHash}", token));
            using var cancelled = await client.PutAsJsonAsync($"/internal/v1/agents/{agentId:D}/assignment", new AgentVersionAssignmentCommand(null, target.Revision, actorId), token);
            Assert.AreEqual(VersionAssignmentResult.Saved, await cancelled.Content.ReadFromJsonAsync<VersionAssignmentResult>(cancellationToken: token));
            var recorded = await client.GetFromJsonAsync<RecordedAgentUpdateTarget>($"/internal/v1/agents/{agentId:D}/update-targets/{target.UpdateId:D}", token);
            Assert.AreEqual(target.PackageId, recorded?.PackageId);
            using var noCurrent = await client.GetAsync($"/internal/v1/agents/{agentId:D}/target", token);
            Assert.AreEqual(HttpStatusCode.NoContent, noCurrent.StatusCode);
            using var revoked = await client.PostAsJsonAsync($"/internal/v1/agents/{agentId:D}/revoke", new AgentRevokeCommand(actorId), token);
            Assert.AreEqual(AgentRegistrationStatus.Success, await revoked.Content.ReadFromJsonAsync<AgentRegistrationStatus>(cancellationToken: token));
            using var afterRevocation = await client.GetAsync($"/internal/v1/agents/{agentId:D}/update/packages/{archiveHash}", token);
            Assert.AreEqual(HttpStatusCode.NotFound, afterRevocation.StatusCode);

            using (var anonymous = await client.GetAsync("/api/v1/devices/", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using (var anonymousPackages = await client.GetAsync("/api/v1/agent-packages", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousPackages.StatusCode);
            var cookieOptions = host.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
            var identityOptions = host.Services.GetRequiredService<IOptions<IdentityOptions>>().Value;
            var claims = new[] { new Claim(identityOptions.ClaimsIdentity.UserIdClaimType, Guid.CreateVersion7().ToString("D")),
                new Claim(identityOptions.ClaimsIdentity.SecurityStampClaimType, "test-stamp") };
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme)),
                new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5) }, IdentityConstants.ApplicationScheme);
            using (var cookieClient = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false }))
            {
                cookieClient.DefaultRequestHeaders.Add("Cookie", $"{cookieOptions.Cookie.Name}={cookieOptions.TicketDataFormat.Protect(ticket)}");
                using var readerCookie = await cookieClient.GetAsync("/api/v1/devices/", token);
                Assert.AreEqual(HttpStatusCode.OK, readerCookie.StatusCode);
                using var deniedAgentManagement = await cookieClient.GetAsync($"/api/v1/agent-management/devices/{active.Id:D}", token);
                Assert.AreEqual(HttpStatusCode.Forbidden, deniedAgentManagement.StatusCode);
                using var deniedTemplates = await cookieClient.GetAsync("/api/v1/admin/publications/templates", token);
                Assert.AreEqual(HttpStatusCode.Forbidden, deniedTemplates.StatusCode);
                identityHandler.Revoked = true;
                using var revokedCookie = await cookieClient.GetAsync("/api/v1/devices/", token);
                Assert.AreEqual(HttpStatusCode.Unauthorized, revokedCookie.StatusCode);
            }
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "vlg_read");
            using (var readerPackages = await client.GetAsync("/api/v1/agent-packages", token)) Assert.AreEqual(HttpStatusCode.OK, readerPackages.StatusCode);
            using (var readerList = await client.GetAsync("/api/v1/devices/", token)) Assert.AreEqual(HttpStatusCode.OK, readerList.StatusCode);
            using (var reader = await client.PostAsJsonAsync("/api/v1/devices/", new DeviceInput("Denied", "denied.example", [], true), token))
                Assert.AreEqual(HttpStatusCode.Forbidden, reader.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "vlg_write");
            using var create = await client.PostAsJsonAsync("/api/v1/devices/",
                new DeviceInput("Managed", "managed.example", [], true), token);
            Assert.AreEqual(HttpStatusCode.Created, create.StatusCode);
            var created = await create.Content.ReadFromJsonAsync<DeviceDetails>(cancellationToken: token);
            Assert.IsNotNull(created);
            using var update = await client.PutAsJsonAsync($"/api/v1/devices/{created.Id:D}",
                new DeviceInput("Managed", "changed.example", [], true, Version: created.Version), token);
            Assert.AreEqual(HttpStatusCode.OK, update.StatusCode);
            var updated = await update.Content.ReadFromJsonAsync<DeviceDetails>(cancellationToken: token);
            Assert.IsNotNull(updated);
            Assert.AreEqual("changed.example", updated.Address);
            using var archive = await client.PostAsJsonAsync($"/api/v1/devices/{created.Id:D}/archive",
                new DeviceVersionRequest(updated.Version), token);
            Assert.AreEqual(HttpStatusCode.OK, archive.StatusCode);
            var archived = await archive.Content.ReadFromJsonAsync<DeviceDetails>(cancellationToken: token);
            Assert.IsNotNull(archived);
            Assert.IsTrue(archived.Archived);
            var archivedAgent = await client.GetFromJsonAsync<DeviceAgentState>($"/internal/v1/devices/{created.Id:D}/agent", token);
            Assert.IsFalse(archivedAgent?.Eligible);
            Assert.AreEqual(1L, archivedAgent?.Generation);
        }, TestContext.CancellationToken);
        }
        finally
        {
            if (Directory.Exists(packagePath)) Directory.Delete(packagePath, recursive: true);
        }
    }

    [AssemblyCleanup]
    public static void Cleanup() => SqlTestDatabase.CompleteRun();

    private sealed class TestCredentialValidation : IApiCredentialValidation
    {
        public Task<VerifiedApiCredentialResponse?> VerifyAsync(string value, CancellationToken token) =>
            Task.FromResult(value is "read" or "write" ? new VerifiedApiCredentialResponse(Guid.CreateVersion7(), value, value == "write") : null);
    }

    private sealed class ReleaseHandler(byte[] archive, string hash) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.Host == "api.github.com")
            {
                var json = $$"""{"tag_name":"agent-v1.2.3","assets":[{"name":"agent-win-x64.zip","size":{{archive.Length}},"digest":"sha256:{{hash}}"}]}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(archive) });
        }
    }

    private sealed class TestIdentityHandler : HttpMessageHandler
    {
        public bool Revoked { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Revoked) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            var role = new IdentityClaim(ClaimTypes.Role, WorkspaceAccess.ReadOnly, ClaimValueTypes.String, "LOCAL AUTHORITY", "LOCAL AUTHORITY");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ValidatedCookieResponse([role])) });
        }
    }
}
