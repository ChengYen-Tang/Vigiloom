extern alias HostMonitoring;
extern alias SecurityHost;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Data.Common;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Security;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.SecurityAnalysis.Application.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class YaraRoutingTests(TestContext context)
{
    [TestMethod]
    public Task HostAuthenticatesAgentBeforeReadingSecurityWork() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var options = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using (var db = new SecurityAnalysisDbContext(options)) await db.Database.MigrateAsync(token);
            var identity = new RegisteredAgentIdentity(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            using var security = new WebApplicationFactory<SecurityHost::Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("Temporal:Enabled", "false");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Security"] = database.ConnectionString }));
                web.ConfigureTestServices(services => services.AddSingleton<IDeviceAgentSource>(new DeviceSource(identity.DeviceId)));
            });
            security.UseKestrel(0);
            using var securityClient = security.CreateClient();
            var inventoryPath = $"/internal/v1/devices/{identity.DeviceId:D}/inventory/";
            var inventoryView = await securityClient.GetFromJsonAsync<DeviceInventoryView>(inventoryPath + $"?agentId={identity.AgentId:D}", token);
            Assert.AreEqual(identity.AgentId, inventoryView!.AgentId);
            var until = DateTimeOffset.UtcNow;
            var query = $"from={Uri.EscapeDataString(until.AddDays(-1).ToString("O"))}&until={Uri.EscapeDataString(until.ToString("O"))}";
            Assert.IsNotNull(await securityClient.GetFromJsonAsync<InventoryChangePage>(inventoryPath + "changes?" + query, token));
            using (var invalidChanges = await securityClient.GetAsync(inventoryPath + "changes?" + query + "&cursor=invalid", token))
                Assert.AreEqual(HttpStatusCode.BadRequest, invalidChanges.StatusCode);
            var before = await securityClient.GetFromJsonAsync<InventorySettingsView>("/internal/v1/inventory-settings", token);
            Assert.IsNotNull(before);
            var actor = Guid.CreateVersion7();
            using (var updated = await securityClient.PutAsJsonAsync("/internal/v1/inventory-settings",
                new { Update = new InventorySettingsUpdate(new(1800, true), before.Version), ActorId = actor }, token))
                Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
            await using (var db = new SecurityAnalysisDbContext(options))
                Assert.AreEqual(actor, (await db.InventorySettingAudits.SingleAsync(token)).ActorId);
            using (var stale = await securityClient.PutAsJsonAsync("/internal/v1/inventory-settings",
                new { Update = new InventorySettingsUpdate(new(3600), before.Version), ActorId = Guid.CreateVersion7() }, token))
                Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
            var scopeInput = new IntegrityScopeInput(IntegrityScopeKind.Directory, "/var/log", true, ImmutableArray<string>.Empty, 3600, 100, 1024, 2048, 4);
            var scopePath = $"/internal/v1/devices/{identity.DeviceId:D}/integrity-scopes/";
            using var scopeResponse = await securityClient.PostAsJsonAsync(scopePath, new { Policy = scopeInput, ActorId = actor }, token);
            scopeResponse.EnsureSuccessStatusCode();
            var scopeResult = await scopeResponse.Content.ReadFromJsonAsync<IntegrityConfigurationResult>(cancellationToken: token);
            Assert.AreEqual(IntegrityConfigurationStatus.Success, scopeResult!.Status);
            Assert.IsNotNull(scopeResult.Scope);
            using var created = await securityClient.PostAsJsonAsync("/internal/v1/yara-scans/create",
                new YaraCreateCommand(identity, new("/tmp/test", "rule test { condition: true }", 1024, 30), Guid.CreateVersion7()), token);
            created.EnsureSuccessStatusCode();
            var job = await created.Content.ReadFromJsonAsync<YaraScanJob>(cancellationToken: token);
            Assert.IsNotNull(job);
            using var host = new WebApplicationFactory<HostMonitoring::Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("Temporal:Enabled", "false");
                web.UseSetting("Hangfire:Enabled", "false");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["Services:Security:Url"] = securityClient.BaseAddress!.ToString() }));
                web.ConfigureTestServices(services => services.AddHttpClient("resources")
                    .ConfigurePrimaryHttpMessageHandler(() => new IdentityHandler(identity)));
            });
            using var agent = host.CreateClient();
            using (var noCredential = await agent.GetAsync("/api/v1/agent/yara-scans/next", token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, noCredential.StatusCode);
            agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid");
            using (var inventory = await agent.GetAsync("/api/v1/agent/inventory-settings?contract=2", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, inventory.StatusCode);
                var snapshot = await inventory.Content.ReadFromJsonAsync<InventorySettingsSnapshot>(cancellationToken: token);
                Assert.IsNotNull(snapshot);
                Assert.AreEqual(1800, snapshot.Settings.ScanIntervalSeconds);
                Assert.IsTrue(snapshot.Settings.CollectCommandLine);
                using var cached = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent/inventory-settings?contract=2");
                cached.Headers.IfNoneMatch.Add(inventory.Headers.ETag!);
                using var unchanged = await agent.SendAsync(cached, token);
                Assert.AreEqual(HttpStatusCode.NotModified, unchanged.StatusCode);
            }
            var legacyInventory = await agent.GetFromJsonAsync<InventorySettingsSnapshot>("/api/v1/agent/inventory-settings", token);
            Assert.IsNotNull(legacyInventory);
            Assert.AreEqual(1800, legacyInventory.Settings.ScanIntervalSeconds);
            Assert.IsFalse(legacyInventory.Settings.CollectCommandLine);
            using (var unsupported = await agent.GetAsync("/api/v1/agent/inventory-settings?contract=3", token))
                Assert.AreEqual(HttpStatusCode.BadRequest, unsupported.StatusCode);
            using (var integrity = await agent.GetAsync("/api/v1/agent/integrity-settings", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, integrity.StatusCode);
                var settings = await integrity.Content.ReadFromJsonAsync<IntegritySettingsSnapshot>(cancellationToken: token);
                Assert.IsNotNull(settings);
                Assert.IsTrue(settings.Scopes.Any(scope => scope.Id == scopeResult.Scope.Scope.Id));
                Assert.IsNotNull(integrity.Headers.ETag);
            }
            var scopeId = scopeResult.Scope.Scope.Id;
            var integrityPath = $"/internal/v1/devices/{identity.DeviceId:D}/integrity/";
            var monitored = await securityClient.GetFromJsonAsync<DeviceIntegrityView>(integrityPath + $"?agentId={identity.AgentId:D}", token);
            Assert.IsTrue(monitored!.Scopes.Any(scope => scope.ScopeId == scopeId));
            Assert.IsNotNull(await securityClient.GetFromJsonAsync<IntegrityChangePage>(integrityPath + "changes?" + query, token));
            using (var invalidIntegrity = await securityClient.GetAsync(integrityPath + "changes?" + query + "&cursor=invalid", token))
                Assert.AreEqual(HttpStatusCode.BadRequest, invalidIntegrity.StatusCode);
            var status = await agent.GetFromJsonAsync<IntegrityScopeStatus>($"/api/v1/agent/integrity-status/{scopeId:D}", token);
            Assert.IsNotNull(status);
            Assert.IsTrue(status.ScopePresent);
            Assert.AreEqual(scopeResult.Scope.Scope.Revision, status.CurrentRevision);
            using (var changed = await securityClient.PutAsJsonAsync(scopePath + scopeId.ToString("D"),
                new { Policy = scopeInput with { ScanIntervalSeconds = 7200 }, scopeResult.Scope.Version, ActorId = actor }, token))
            {
                var result = await changed.Content.ReadFromJsonAsync<IntegrityConfigurationResult>(cancellationToken: token);
                Assert.AreEqual(IntegrityConfigurationStatus.Success, result!.Status);
                scopeResult = result;
            }
            var updatedIntegrity = await agent.GetFromJsonAsync<IntegritySettingsSnapshot>("/api/v1/agent/integrity-settings", token);
            Assert.AreEqual(7200, updatedIntegrity!.Scopes.Single(scope => scope.Id == scopeId).ScanIntervalSeconds);
            using (var retired = await securityClient.PostAsJsonAsync(scopePath + $"{scopeId:D}/retire",
                new { scopeResult.Scope!.Version, ActorId = actor }, token))
                Assert.AreEqual(IntegrityConfigurationStatus.Success,
                    (await retired.Content.ReadFromJsonAsync<IntegrityConfigurationResult>(cancellationToken: token))!.Status);
            var retiredIntegrity = await agent.GetFromJsonAsync<IntegritySettingsSnapshot>("/api/v1/agent/integrity-settings", token);
            Assert.IsFalse(retiredIntegrity!.Scopes.Any(scope => scope.Id == scopeId));
            var retiredStatus = await agent.GetFromJsonAsync<IntegrityScopeStatus>($"/api/v1/agent/integrity-status/{scopeId:D}", token);
            Assert.IsFalse(retiredStatus!.ScopePresent);
            using (var baseline = await agent.GetAsync("/api/v1/agent/baseline-settings", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, baseline.StatusCode);
                var baselineSettings = await baseline.Content.ReadFromJsonAsync<BaselineSettingsSnapshot>(cancellationToken: token);
                Assert.IsNotNull(baselineSettings);
                Assert.AreEqual(1800, baselineSettings.ScanIntervalSeconds);
                Assert.IsTrue(baseline.Headers.CacheControl!.NoStore);
            }
            Assert.AreEqual(job.Id, (await agent.GetFromJsonAsync<YaraScanJob>("/api/v1/agent/yara-scans/next", token))!.Id);
            using (var cancel = await securityClient.PostAsync($"/internal/v1/yara-scans/devices/{identity.DeviceId:D}/{job.Id:D}/cancel", null, token))
                Assert.AreEqual(HttpStatusCode.NoContent, cancel.StatusCode);
            using (var next = await agent.GetAsync("/api/v1/agent/yara-scans/next", token)) Assert.AreEqual(HttpStatusCode.NoContent, next.StatusCode);
            agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "revoked");
            using var denied = await agent.GetAsync("/api/v1/agent/yara-scans/next", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            using var deniedSettings = await agent.GetAsync("/api/v1/agent/integrity-settings", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, deniedSettings.StatusCode);
            using var deniedBaseline = await agent.GetAsync("/api/v1/agent/baseline-settings", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, deniedBaseline.StatusCode);
            using var deniedStatus = await agent.GetAsync($"/api/v1/agent/integrity-status/{scopeId:D}", token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, deniedStatus.StatusCode);
        }, context.CancellationToken);

    private sealed class IdentityHandler(RegisteredAgentIdentity identity) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var credential = await request.Content!.ReadFromJsonAsync<AgentCredentialCommand>(cancellationToken: token);
            return credential?.Credential == "valid" ? new(HttpStatusCode.OK) { Content = JsonContent.Create(identity) } : new(HttpStatusCode.Unauthorized);
        }
    }

    private sealed class DeviceSource(Guid deviceId) : IDeviceAgentSource
    {
        public Task<DeviceAgentState?> GetAsync(Guid id, CancellationToken token) =>
            Task.FromResult<DeviceAgentState?>(id == deviceId ? new(id, 1, true) : null);
        public Task<bool> IsCurrentAsync(Guid id, long generation, DbConnection connection, DbTransaction transaction, CancellationToken token) =>
            throw new NotSupportedException();
    }
}
