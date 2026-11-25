using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Claims;
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
using RabbitMQ.Client;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Identity;
using Vigiloom.Contracts.Resources;
using Vigiloom.Contracts.Workspace;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;

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
        var broker = Environment.GetEnvironmentVariable("VIGILOOM_TEST_RABBITMQ");
        if (string.IsNullOrWhiteSpace(broker)) Assert.Inconclusive("Set VIGILOOM_TEST_RABBITMQ for the RabbitMQ integration test.");
        var suffix = Guid.CreateVersion7().ToString("N");
        var networkQueue = "vigiloom-test-network-" + suffix;
        var agentQueue = "vigiloom-test-agent-" + suffix;
        var auditQueue = "vigiloom-test-audit-" + suffix;
        var observabilityQueue = "vigiloom-test-observation-" + suffix;
        var identityHandler = new TestIdentityHandler();
        try
        {
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("ConnectionStrings:Resources", database.ConnectionString);
                builder.UseSetting("ConnectionStrings:Messaging", broker);
                builder.UseSetting("Messaging:NetworkQueue", networkQueue);
                builder.UseSetting("Messaging:AgentQueue", agentQueue);
                builder.UseSetting("Messaging:AuditQueue", auditQueue);
                builder.UseSetting("Messaging:ObservabilityQueue", observabilityQueue);
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IApiCredentialValidation>();
                    services.AddSingleton<IApiCredentialValidation, TestCredentialValidation>();
                    services.AddHttpClient(nameof(IdentityCookieValidator)).ConfigurePrimaryHttpMessageHandler(() => identityHandler);
                });
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ConnectionStrings:Resources"] = database.ConnectionString, ["ConnectionStrings:Messaging"] = broker,
                        ["Messaging:NetworkQueue"] = networkQueue, ["Messaging:AgentQueue"] = agentQueue }));
            });
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

            using (var anonymous = await client.GetAsync("/api/v1/devices/", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
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
                identityHandler.Revoked = true;
                using var revokedCookie = await cookieClient.GetAsync("/api/v1/devices/", token);
                Assert.AreEqual(HttpStatusCode.Unauthorized, revokedCookie.StatusCode);
            }
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "vlg_read");
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
            var factory = new ConnectionFactory { Uri = new Uri(broker!) };
            await using var connection = await factory.CreateConnectionAsync(CancellationToken.None);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: CancellationToken.None);
            await channel.QueueDeleteAsync(networkQueue, cancellationToken: CancellationToken.None);
            await channel.QueueDeleteAsync(agentQueue, cancellationToken: CancellationToken.None);
            await channel.QueueDeleteAsync(auditQueue, cancellationToken: CancellationToken.None);
            await channel.QueueDeleteAsync(observabilityQueue, cancellationToken: CancellationToken.None);
        }
    }

    [AssemblyCleanup]
    public static void Cleanup() => SqlTestDatabase.CompleteRun();

    private sealed class TestCredentialValidation : IApiCredentialValidation
    {
        public Task<VerifiedApiCredentialResponse?> VerifyAsync(string value, CancellationToken token) =>
            Task.FromResult(value is "read" or "write" ? new VerifiedApiCredentialResponse(Guid.CreateVersion7(), value, value == "write") : null);
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
