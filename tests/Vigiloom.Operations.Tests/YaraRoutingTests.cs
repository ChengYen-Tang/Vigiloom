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
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Vulnerabilities;
using Vigiloom.Modules.SecurityAnalysis.Application.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Inventory;
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
            var anotherDevice = Guid.CreateVersion7();
            await using (var db = new SecurityAnalysisDbContext(options))
            {
                db.InventorySources.Add(new InventorySourceState(new(identity.AgentId, identity.DeviceId, 1), InventorySource.HostSystem));
                db.InventorySources.Add(new InventorySourceState(new(Guid.CreateVersion7(), anotherDevice, 1), InventorySource.LinuxDpkg));
                await db.SaveChangesAsync(token);
            }
            using var security = new WebApplicationFactory<SecurityHost::Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("Temporal:Enabled", "false");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Security"] = database.ConnectionString }));
                web.ConfigureTestServices(services => services.AddSingleton<IDeviceAgentSource>(new DeviceSource(identity.DeviceId)));
                web.ConfigureTestServices(services => services.AddSingleton<IAgentOwnerResolver>(new NoAgentOwner()));
                web.ConfigureTestServices(services =>
                {
                    services.AddHttpClient("host").ConfigurePrimaryHttpMessageHandler(() => new BaselineSourceHandler());
                    services.AddHttpClient("operations").ConfigurePrimaryHttpMessageHandler(() => new BaselineSourceHandler());
                });
            });
            security.UseKestrel(0);
            using var securityClient = security.CreateClient();
            Assert.IsNotNull(await securityClient.GetFromJsonAsync<IntelligenceSourcePage>("/internal/v1/intelligence/sources/?page=1", token));
            var custom = new BaselineCheck("", "Test", "linux", ["debian:12"], BaselineSources.LinuxShadowOperation, "/etc/shadow", "0640", "", "local");
            using (var staged = await securityClient.PostAsJsonAsync("/internal/v1/intelligence/custom-baselines/stage",
                new { SourceId = (Guid?)null, ExpectedVersion = (string?)null, Check = custom, ActorId = Guid.CreateVersion7() }, token))
            {
                staged.EnsureSuccessStatusCode();
                var result = await staged.Content.ReadFromJsonAsync<CustomBaselineStageResult>(cancellationToken: token);
                Assert.AreEqual(CustomBaselineStageStatus.Staged, result!.Status);
                var saved = await securityClient.GetFromJsonAsync<CustomBaselineDraft>($"/internal/v1/intelligence/custom-baselines/{result.SourceId:D}/revisions/{result.RevisionId:D}/draft", token);
                Assert.AreEqual(custom.Title, saved!.Check.Title);
                var active = await securityClient.GetFromJsonAsync<BaselineSourceSnapshot>($"/internal/v1/intelligence/custom-baselines/{result.SourceId:D}/active", token);
                Assert.AreEqual(BaselineSourceAvailability.NoActiveRevision, active!.Availability);
            }
            using (var invalidPage = await securityClient.GetAsync("/internal/v1/intelligence/sources/?page=0", token))
                Assert.AreEqual(HttpStatusCode.BadRequest, invalidPage.StatusCode);
            using (var noAudit = await securityClient.PostAsJsonAsync($"/internal/v1/intelligence/sources/{Guid.CreateVersion7():D}/activation",
                new { Input = new IntelligenceActivationRequest(Guid.CreateVersion7(), null, "", false), ActorId = Guid.CreateVersion7() }, token))
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, noAudit.StatusCode);
            var sourcePath = $"/internal/v1/intelligence/sources/{Guid.CreateVersion7():D}/";
            using (var noPolicyAudit = await securityClient.PutAsJsonAsync(sourcePath + "policy",
                new { Input = new IntelligencePolicyUpdate(true, 3600, 7200, 1024, 100, ""), ActorId = Guid.CreateVersion7() }, token))
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, noPolicyAudit.StatusCode);
            using (var noCredentialAudit = await securityClient.PutAsJsonAsync(sourcePath + "advisory-credential",
                new { Input = new AdvisoryCredentialUpdate("", true, null), ActorId = Guid.CreateVersion7() }, token))
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, noCredentialAudit.StatusCode);
            using (var noImportAudit = await securityClient.PostAsync(sourcePath + $"offline-import?actorId={Guid.CreateVersion7():D}", new StringContent("{}"), token))
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, noImportAudit.StatusCode);
            var baselineView = await securityClient.GetFromJsonAsync<DeviceBaselineView>($"/internal/v1/devices/{identity.DeviceId:D}/baseline", token);
            Assert.IsNotNull(baselineView);
            Assert.AreEqual(identity.DeviceId, baselineView.DeviceId);
            using (var noAudit = await securityClient.PostAsJsonAsync($"/internal/v1/devices/{identity.DeviceId:D}/baseline-exceptions",
                new { Input = new BaselineExceptionInput("test", Guid.CreateVersion7(), "reason"), ActorId = Guid.CreateVersion7() }, token))
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, noAudit.StatusCode);
            using (var count = await securityClient.PostAsJsonAsync("/internal/v1/retention/detections/count", new SecurityDetectionRetentionRequest(DateTimeOffset.UtcNow, 30), token))
            {
                count.EnsureSuccessStatusCode();
                Assert.AreEqual(0L, await count.Content.ReadFromJsonAsync<long>(cancellationToken: token));
            }
            using (var invalidRetention = await securityClient.PostAsJsonAsync("/internal/v1/retention/detections/delete", new SecurityDetectionRetentionRequest(DateTimeOffset.UtcNow, 0, 500), token))
                Assert.AreEqual(HttpStatusCode.BadRequest, invalidRetention.StatusCode);
            var inventoryPath = $"/internal/v1/devices/{identity.DeviceId:D}/inventory/";
            var inventoryView = await securityClient.GetFromJsonAsync<DeviceInventoryView>(inventoryPath + $"?agentId={identity.AgentId:D}", token);
            Assert.AreEqual(identity.AgentId, inventoryView!.AgentId);
            Assert.HasCount(1, inventoryView.Sources);
            Assert.AreEqual(InventorySource.HostSystem, inventoryView.Sources[0].Source);
            var otherInventory = await securityClient.GetFromJsonAsync<DeviceInventoryView>(
                $"/internal/v1/devices/{anotherDevice:D}/inventory/?agentId={identity.AgentId:D}", token);
            Assert.IsEmpty(otherInventory!.Sources);
            var until = DateTimeOffset.UtcNow;
            var query = $"from={Uri.EscapeDataString(until.AddDays(-1).ToString("O"))}&until={Uri.EscapeDataString(until.ToString("O"))}";
            var detectionPath = $"/internal/v1/devices/{identity.DeviceId:D}/detections/";
            var vulnerabilityPath = $"/internal/v1/devices/{identity.DeviceId:D}/vulnerabilities/";
            var vulnerabilities = await securityClient.GetFromJsonAsync<CurrentVulnerabilityPage>(vulnerabilityPath + "?page=1&gapPage=1", token);
            Assert.AreEqual(VulnerabilityCoverageStatus.Missing, vulnerabilities!.Status);
            using (var invalidVulnerabilities = await securityClient.GetAsync(vulnerabilityPath + "?page=0&gapPage=1", token))
                Assert.AreEqual(HttpStatusCode.BadRequest, invalidVulnerabilities.StatusCode);
            Assert.IsNotNull(await securityClient.GetFromJsonAsync<SigmaDetectionEventPage>(detectionPath + "sigma?" + query, token));
            Assert.IsNotNull(await securityClient.GetFromJsonAsync<SigmaDetectionGapPage>(detectionPath + "sigma-gaps?" + query, token));
            Assert.IsNotNull(await securityClient.GetFromJsonAsync<IocDetectionPage>(detectionPath + "ioc?" + query + "&page=1", token));
            using (var invalidDetection = await securityClient.GetAsync(detectionPath + "ioc?" + query + "&page=0", token))
                Assert.AreEqual(HttpStatusCode.BadRequest, invalidDetection.StatusCode);
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
            await using (var db = new SecurityAnalysisDbContext(options))
                await db.InventorySettingAudits.ExecuteUpdateAsync(update => update.SetProperty(x => x.OccurredAt, DateTimeOffset.UtcNow.AddDays(-40)), token);
            var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
            using (var countAudit = await securityClient.PostAsJsonAsync("/internal/v1/retention/audit/count", new SecurityAuditRetentionRequest(cutoff), token))
            {
                countAudit.EnsureSuccessStatusCode();
                Assert.AreEqual(1L, await countAudit.Content.ReadFromJsonAsync<long>(cancellationToken: token));
            }
            using (var deleteAudit = await securityClient.PostAsJsonAsync("/internal/v1/retention/audit/delete", new SecurityAuditRetentionRequest(cutoff, 500), token))
            {
                deleteAudit.EnsureSuccessStatusCode();
                Assert.AreEqual(1, await deleteAudit.Content.ReadFromJsonAsync<int>(cancellationToken: token));
            }
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
    private sealed class NoAgentOwner : IAgentOwnerResolver
    {
        public Task<InventoryOwner?> ResolveAsync(Guid deviceId, CancellationToken token) => Task.FromResult<InventoryOwner?>(null);
    }
    private sealed class BaselineSourceHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(request.RequestUri!.AbsolutePath.Contains("agent-security-observation", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<BaselineExceptionNote>()) });
    }
}
