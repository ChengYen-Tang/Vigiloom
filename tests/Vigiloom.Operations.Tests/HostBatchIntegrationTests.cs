extern alias HostMonitoring;
extern alias GatewayHost;
extern alias SecurityHost;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text.Json;
using System.Security.Claims;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Security;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Contracts.Operations;
using Vigiloom.Contracts.Identity;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Resources;
using Vigiloom.Infrastructure.Authentication;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Infrastructure.Persistence;
using Vigiloom.Infrastructure.Scheduling.Outbox;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Infrastructure.Work;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Domain.Detection;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Operations.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class HostBatchIntegrationTests(TestContext context)
{
    [TestMethod]
    public Task GatewayHostAndSecurityShareInventoryProgress() => RunInventoryPipelineAsync(null);

    [TestMethod]
    public Task TemporalDeliversAgentInventoryFromHostToSecurity()
    {
        var address = Environment.GetEnvironmentVariable("VIGILOOM_TEST_TEMPORAL_ISOLATED");
        if (string.IsNullOrWhiteSpace(address)) Assert.Inconclusive("Set VIGILOOM_TEST_TEMPORAL_ISOLATED to a disposable Temporal instance for this integration test.");
        return RunInventoryPipelineAsync(address);
    }

    private Task RunInventoryPipelineAsync(string? temporalAddress) =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (hostDb, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (securityDb, token) =>
        {
            var hostOptions = new DbContextOptionsBuilder<HostMonitoringDbContext>().UseSqlServer(hostDb.ConnectionString,
                sql => sql.MigrationsHistoryTable(HostMonitoringDbContext.MigrationHistory, "agent")).Options;
            await using (var db = new HostMonitoringDbContext(hostOptions))
            {
                await db.Database.MigrateAsync(token);
                var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
                var policy = new AgentReplayPolicy(); policy.InitializeFresh(now); policy.BeginCleanupCutover(now);
                db.ReplayPolicies.Add(policy);
                await db.SaveChangesAsync(token);
            }
            var hostScheduling = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(hostDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new SchedulingDbContext(hostScheduling)) await db.Database.MigrateAsync(token);
            var securityOptions = new DbContextOptionsBuilder<SecurityAnalysisDbContext>().UseSqlServer(securityDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "security")).Options;
            await using (var db = new SecurityAnalysisDbContext(securityOptions)) await db.Database.MigrateAsync(token);
            var scheduling = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(securityDb.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
            await using (var db = new SchedulingDbContext(scheduling)) await db.Database.MigrateAsync(token);
            var queue = "vigiloom-security-test-" + Guid.CreateVersion7().ToString("N");
            HttpClient? hostClientForWorker = null;
            var identity = new RegisteredAgentIdentity(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            var auditHandler = new AuditHandler();
            var sessionHandler = new AdminSessionHandler();
            using var security = new WebApplicationFactory<SecurityHost::Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("Temporal:Enabled", temporalAddress is null ? "false" : "true");
                if (temporalAddress is not null) web.UseSetting("Temporal:Address", temporalAddress);
                web.UseSetting("Work:SecurityQueue", queue);
                web.UseSetting("Work:SecurityLogQueue", queue + "-logs");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Security"] = securityDb.ConnectionString, ["Temporal:Address"] = temporalAddress, ["Work:SecurityQueue"] = queue }));
                web.ConfigureTestServices(services =>
                {
                    services.AddHttpClient("ResourceDevices").ConfigurePrimaryHttpMessageHandler(() => new IdentityHandler(identity));
                    services.AddHttpClient("observability").ConfigurePrimaryHttpMessageHandler(() => new RetentionHandler(21));
                    services.AddHttpClient("operations").ConfigurePrimaryHttpMessageHandler(() => auditHandler);
                    services.AddHttpClient<IdentityCookieValidator>().ConfigurePrimaryHttpMessageHandler(() => sessionHandler);
                    services.RemoveAll<IApiCredentialValidation>();
                    services.AddSingleton<IApiCredentialValidation, CredentialValidation>();
                });
                web.ConfigureTestServices(services => services.AddHttpClient("host")
                    .ConfigurePrimaryHttpMessageHandler(() => new DeferredHostHandler(() => hostClientForWorker!)));
            });
            using var securityClient = security.CreateClient();
            using (var apiDocument = await securityClient.GetFromJsonAsync<JsonDocument>("/openapi/v1.json", token))
            {
                var paths = apiDocument!.RootElement.GetProperty("paths");
                Assert.IsFalse(paths.EnumerateObject().Any(path => path.Name.StartsWith("/internal/", StringComparison.Ordinal)));
                var read = paths.GetProperty("/api/v1/devices/{deviceId}/baseline").GetProperty("get").GetProperty("security");
                Assert.AreEqual(2, read.GetArrayLength());
                var inventoryResponse = paths.GetProperty("/api/v1/devices/{deviceId}/inventory").GetProperty("get").GetProperty("responses").GetProperty("200");
                Assert.IsTrue(inventoryResponse.GetProperty("content").GetProperty("application/json").TryGetProperty("schema", out _));
                var inventorySecurity = paths.GetProperty("/api/v1/devices/{deviceId}/inventory").GetProperty("get").GetProperty("security");
                Assert.IsTrue(inventorySecurity.EnumerateArray().Any(x => x.TryGetProperty("BrowserSession", out _)));
                Assert.IsTrue(inventorySecurity.EnumerateArray().Any(x => x.TryGetProperty("ApiBearer", out _)));
                Assert.IsFalse(inventorySecurity.EnumerateArray().Any(x => x.TryGetProperty("AgentBearer", out _)));
                var write = paths.GetProperty("/api/v1/agent-management/inventory-settings/platform").GetProperty("put").GetProperty("security");
                Assert.AreEqual(1, write.GetArrayLength());
                Assert.IsTrue(write[0].TryGetProperty("BrowserSession", out _));
                Assert.IsTrue(write[0].TryGetProperty("Antiforgery", out _));
                var sigmaRead = paths.GetProperty("/api/v1/admin/sigma/rules").GetProperty("get").GetProperty("security");
                Assert.IsTrue(sigmaRead[0].TryGetProperty("BrowserSession", out _));
                Assert.IsFalse(sigmaRead[0].TryGetProperty("ApiBearer", out _));
                foreach (var sigmaPath in new[] { ("/api/v1/admin/sigma/tests", "post"), ("/api/v1/admin/sigma/state", "put") })
                {
                    var requirement = paths.GetProperty(sigmaPath.Item1).GetProperty(sigmaPath.Item2).GetProperty("security")[0];
                    Assert.IsTrue(requirement.TryGetProperty("BrowserSession", out _));
                    Assert.IsTrue(requirement.TryGetProperty("Antiforgery", out _));
                    Assert.IsFalse(requirement.TryGetProperty("ApiBearer", out _));
                }
            }
            var writer = new SecurityWriter();
            using var host = new WebApplicationFactory<HostMonitoring::Program>().WithWebHostBuilder(web =>
            {
                web.UseUrls("http://127.0.0.1:0");
                web.UseSetting("Hangfire:Enabled", "false"); web.UseSetting("Temporal:Enabled", temporalAddress is null ? "false" : "true");
                if (temporalAddress is not null) web.UseSetting("Temporal:Address", temporalAddress);
                web.UseSetting("Work:SecurityQueue", queue);
                web.UseSetting("Work:AgentQueue", queue + "-agent");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:HostMonitoring"] = hostDb.ConnectionString, ["Services:Security:Url"] = securityClient.BaseAddress!.ToString(),
                    ["Temporal:Address"] = temporalAddress, ["Work:SecurityQueue"] = queue }));
                web.ConfigureTestServices(services =>
                {
                    services.AddHttpClient("resources").ConfigurePrimaryHttpMessageHandler(() => new IdentityHandler(identity));
                    services.AddHttpClient("security").ConfigurePrimaryHttpMessageHandler(() => security.Server.CreateHandler());
                    if (temporalAddress is null) services.AddSingleton<IHostSecurityWorkWriter>(writer);
                });
            });
            host.UseKestrel(0);
            using var hostClient = host.CreateClient();
            hostClientForWorker = hostClient;
            var identityBuilder = WebApplication.CreateBuilder(); identityBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var identityService = identityBuilder.Build();
            identityService.MapGet("/api/v1/agent/identity", () => Results.Json(identity));
            await identityService.StartAsync(token);
            using var gateway = new WebApplicationFactory<GatewayHost::Program>().WithWebHostBuilder(web => web.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Vigiloom:Gateway:AgentIdentityAddress"] = identityService.Urls.Single(),
                    ["ReverseProxy:Clusters:host-monitoring:Destinations:host-monitoring:Address"] = hostClient.BaseAddress!.ToString()
                })));
            using var client = gateway.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid");
            InventoryEntry[] items = [new("pkg", "pkg", "1", "x64")];
            var measured = InventoryDigest.MeasureChunk(items); var nowUtc = DateTimeOffset.UtcNow;
            var chunk = new InventoryChunk(4, Guid.CreateVersion7(), nowUtc, Guid.CreateVersion7(), 1, InventorySource.LinuxDpkg,
                nowUtc, InventoryOutcome.Complete, 0, 1, 1, measured.Bytes, InventoryDigest.Combine([measured.Digest]), items);
            var payload = InventoryChunkCodec.Serialize(chunk);
            using (var accepted = await SendAsync(client, payload, "valid", token)) Assert.AreEqual(HttpStatusCode.NoContent, accepted.StatusCode);
            using (var duplicate = await SendAsync(client, payload, "valid", token)) Assert.AreEqual(HttpStatusCode.NoContent, duplicate.StatusCode);
            if (temporalAddress is null)
            {
                var reference = Assert.ContainsSingle(writer.Batches);
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Security"] = securityDb.ConnectionString }).Build();
                var worker = new SecurityHost::Vigiloom.Security.SecurityWorkHandler(config, new SingleHttpClientFactory(hostClient));
                var work = new ServiceWork(nameof(HostSecurityBatchReady), JsonSerializer.Serialize(reference), Guid.CreateVersion7().ToString());
                await worker.ExecuteAsync(work, token);
                await worker.ExecuteAsync(work, token);
            }
            else
            {
                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (true)
                {
                    await using var db = new SecurityAnalysisDbContext(securityOptions);
                    await using var hostState = new HostMonitoringDbContext(hostOptions);
                    if (await db.InventorySnapshots.AnyAsync(token) && !await hostState.HostSecurityBatches.AnyAsync(token)) break;
                    if (DateTime.UtcNow >= deadline)
                    {
                        await using var connection = new SqlConnection(hostDb.ConnectionString);
                        await connection.OpenAsync(token);
                        await using var command = new SqlCommand("SELECT CONCAT(State, ':', Attempts, ':', COALESCE(LastFailureCode, '')) FROM scheduling.Outbox", connection);
                        Assert.Fail($"Temporal did not deliver the inventory batch. Host outbox: {await command.ExecuteScalarAsync(token) ?? "empty"}");
                    }
                    await Task.Delay(250, token);
                }
            }
            var progress = await client.GetFromJsonAsync<InventorySourceStatus[]>("/api/v1/agent/inventory/status?contract=5", token);
            Assert.AreEqual(1L, progress!.Single(x => x.Source == InventorySource.LinuxDpkg).HighWaterGeneration);
            await using (var db = new SecurityAnalysisDbContext(securityOptions)) Assert.AreEqual(1, await db.InventorySnapshots.CountAsync(token));
            await using (var db = new HostMonitoringDbContext(hostOptions)) Assert.AreEqual(0, await db.HostSecurityBatches.CountAsync(token));
            InventoryEntry[] nextItems = [new("private-package", "Private software", "1.0", "x64", SourcePackageName: "package-source", SourcePackageVersion: "1.0")];
            var nextMeasured = InventoryDigest.MeasureChunk(nextItems);
            var next = chunk with { SchemaVersion = InventoryChunkCodec.DpkgSourceSchemaVersion, BatchId = Guid.CreateVersion7(), SnapshotId = Guid.CreateVersion7(),
                Generation = 2, TotalItems = 1, TotalBytes = nextMeasured.Bytes, Digest = InventoryDigest.Combine([nextMeasured.Digest]), Items = nextItems };
            async Task PublishInventoryAsync(InventoryChunk batch)
            {
                using (var accepted = await SendAsync(client, InventoryChunkCodec.Serialize(batch), "valid", token))
                    Assert.AreEqual(HttpStatusCode.NoContent, accepted.StatusCode);
                if (temporalAddress is null)
                {
                    var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Security"] = securityDb.ConnectionString }).Build();
                    var worker = new SecurityHost::Vigiloom.Security.SecurityWorkHandler(config, new SingleHttpClientFactory(hostClient));
                    await worker.ExecuteAsync(new ServiceWork(nameof(HostSecurityBatchReady), JsonSerializer.Serialize(writer.Batches[^1]), Guid.CreateVersion7().ToString()), token);
                }
                else
                {
                    var deadline = DateTime.UtcNow.AddSeconds(45);
                    while (true)
                    {
                        await using var db = new SecurityAnalysisDbContext(securityOptions);
                        if (await db.InventorySources.AnyAsync(x => x.AgentId == identity.AgentId && x.Source == batch.Source && x.CurrentGeneration == batch.Generation, token)) break;
                        if (DateTime.UtcNow >= deadline) Assert.Fail("Temporal did not deliver the inventory snapshot.");
                        await Task.Delay(250, token);
                    }
                }
            }
            await PublishInventoryAsync(next);
            InventoryEntry[] processItems = [new("linux:boot:42:1", "worker", NumericId: 42, ProcessStartedAt: nowUtc)];
            var processMeasured = InventoryDigest.MeasureChunk(processItems);
            var process = new InventoryChunk(5, Guid.CreateVersion7(), nowUtc, Guid.CreateVersion7(), 1, InventorySource.HostProcesses,
                nowUtc, InventoryOutcome.Complete, 0, 1, 1, processMeasured.Bytes, InventoryDigest.Combine([processMeasured.Digest]), processItems);
            await PublishInventoryAsync(process);
            await using (var db = new SecurityAnalysisDbContext(securityOptions))
            {
                var feed = new IntelligenceSource { Key = "private-detection-source", Enabled = true };
                var revision = new IntelligenceRevision { SourceId = feed.Id, UpstreamVersion = "test", RetrievedAt = nowUtc,
                    Digest = new byte[32], Artifact = [], SchemaVersion = "test", MappingVersion = "test", LicenseEvidence = "test" };
                var rule = new SigmaRuleTest { Id = Guid.CreateVersion7(), SourceId = feed.Id, RevisionId = revision.Id,
                    Path = "rules/private-detection.yml", Digest = new string('a', 64), ActivationVersion = "v1", MatcherVersion = "1",
                    SamplesJson = "[]", Passed = true, ActorId = Guid.CreateVersion7(), TestedAt = nowUtc };
                var activation = new SigmaRuleActivation { Id = Guid.CreateVersion7(), SourceId = feed.Id, Path = rule.Path,
                    TestId = rule.Id, Enabled = true, UpdatedBy = Guid.CreateVersion7(), UpdatedAt = nowUtc };
                db.IntelligenceSources.Add(feed); db.IntelligenceRevisions.Add(revision); db.SigmaRuleTests.Add(rule); db.SigmaRuleActivations.Add(activation);
                db.SigmaDetectionResults.Add(new SigmaDetectionResult { AgentId = identity.AgentId, DeviceId = identity.DeviceId,
                    DeviceGeneration = identity.DeviceGeneration, MessageId = Guid.CreateVersion7(), ActivationId = activation.Id,
                    TestId = rule.Id, ActivationVersion = new byte[8], Result = (int)SigmaExpectedOutcome.Match,
                    OccurredAt = nowUtc, ReceivedAt = nowUtc, EvaluatedAt = nowUtc });
                await db.SaveChangesAsync(token);
            }
            using (var securityApi = security.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") }))
            {
                using (var denied = await securityApi.GetAsync($"/api/v1/devices/{identity.DeviceId:D}/inventory", token))
                    Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
                using (var denied = await securityApi.GetAsync($"/api/v1/devices/{identity.DeviceId:D}/integrity", token))
                    Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
                using (var denied = await securityApi.GetAsync($"/api/v1/devices/{identity.DeviceId:D}/vulnerabilities", token))
                    Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
                using (var denied = await securityApi.GetAsync($"/api/v1/devices/{identity.DeviceId:D}/sigma-detections?from={Uri.EscapeDataString(nowUtc.AddMinutes(-1).ToString("O"))}&until={Uri.EscapeDataString(nowUtc.AddMinutes(1).ToString("O"))}", token))
                    Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
                using (var denied = await securityApi.GetAsync($"/api/v1/devices/{identity.DeviceId:D}/inventory/{(int)process.Source}/snapshots/{process.SnapshotId:D}/chunks/0", token))
                    Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
                securityApi.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "vlg_valid");
                using (var denied = await securityApi.GetAsync($"/api/v1/agent-management/devices/{identity.DeviceId:D}/integrity-scopes", token))
                    Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
                using (var denied = await securityApi.GetAsync("/api/v1/admin/intelligence/sources", token))
                    Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
                var inventory = await securityApi.GetFromJsonAsync<DeviceInventoryView>($"/api/v1/devices/{identity.DeviceId:D}/inventory", token);
                Assert.AreEqual(identity.AgentId, inventory!.AgentId);
                Assert.IsTrue(inventory.Sources.Any(source => source.Source == InventorySource.LinuxDpkg));
                var source = inventory.Sources.Single(source => source.Source == InventorySource.LinuxDpkg);
                Assert.AreEqual(next.SnapshotId, source.CurrentSnapshotId);
                Assert.AreEqual(1, source.CurrentItemCount);
                var currentChunk = await securityApi.GetFromJsonAsync<InventorySnapshotChunkView>($"/api/v1/devices/{identity.DeviceId:D}/inventory/{(int)next.Source}/snapshots/{next.SnapshotId:D}/chunks/0", token);
                Assert.AreEqual("Private software", currentChunk!.Items[0].Name);
                Assert.AreEqual("package-source", currentChunk.Items[0].SourcePackageName);
                Assert.AreEqual("1.0", currentChunk.Items[0].SourcePackageVersion);
                var processChunk = await securityApi.GetFromJsonAsync<InventorySnapshotChunkView>($"/api/v1/devices/{identity.DeviceId:D}/inventory/{(int)process.Source}/snapshots/{process.SnapshotId:D}/chunks/0", token);
                Assert.AreEqual(42U, processChunk!.Items.Single().NumericId);
                using (var stale = await securityApi.GetAsync($"/api/v1/devices/{identity.DeviceId:D}/inventory/{(int)chunk.Source}/snapshots/{chunk.SnapshotId:D}/chunks/0", token))
                    Assert.AreEqual(HttpStatusCode.NotFound, stale.StatusCode);
                using (var vulnerability = await securityApi.GetAsync($"/api/v1/devices/{identity.DeviceId:D}/vulnerabilities", token))
                    Assert.AreEqual(HttpStatusCode.OK, vulnerability.StatusCode);
                var start = Uri.EscapeDataString(nowUtc.AddMinutes(-1).ToString("O"));
                var end = Uri.EscapeDataString(nowUtc.AddMinutes(1).ToString("O"));
                var changes = await securityApi.GetFromJsonAsync<InventoryChangePage>($"/api/v1/devices/{identity.DeviceId:D}/inventory/changes?from={start}&until={end}", token);
                var added = Assert.ContainsSingle(changes!.Items.Where(item => item.After?.Name == "Private software"));
                Assert.AreEqual(InventoryChangeType.Added, added.Kind);
                Assert.AreEqual("package-source", added.After!.SourcePackageName);
                Assert.AreEqual("1.0", added.After.SourcePackageVersion);
                using (var integrityResponse = await securityApi.GetAsync($"/api/v1/devices/{identity.DeviceId:D}/integrity", token))
                    Assert.AreEqual(HttpStatusCode.OK, integrityResponse.StatusCode);
                using (var baselineResponse = await securityApi.GetAsync($"/api/v1/devices/{identity.DeviceId:D}/baseline", token))
                    Assert.AreEqual(HttpStatusCode.OK, baselineResponse.StatusCode);
                using (var integrityChanges = await securityApi.GetAsync($"/api/v1/devices/{identity.DeviceId:D}/integrity/changes?from={start}&until={end}", token))
                    Assert.AreEqual(HttpStatusCode.OK, integrityChanges.StatusCode);
                foreach (var path in new[] { "sigma-detections", "sigma-gaps", "ioc-detections" })
                {
                    using var detection = await securityApi.GetAsync($"/api/v1/devices/{identity.DeviceId:D}/{path}?from={start}&until={end}", token);
                    Assert.AreEqual(HttpStatusCode.OK, detection.StatusCode);
                    if (path == "ioc-detections")
                        Assert.AreEqual(21, (await detection.Content.ReadFromJsonAsync<Vigiloom.Contracts.Intelligence.IocDetectionPage>(cancellationToken: token))!.RetentionDays);
                }
                using (var yara = await securityApi.GetAsync($"/api/v1/devices/{identity.DeviceId:D}/yara-scans", token))
                    Assert.AreEqual(HttpStatusCode.OK, yara.StatusCode);
                Assert.AreEqual(1, auditHandler.Calls);
                securityApi.DefaultRequestHeaders.Authorization = null;
                var cookieOptions = security.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
                var claims = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Guid.CreateVersion7().ToString("D")),
                    new Claim("AspNet.Identity.SecurityStamp", "stamp")], IdentityConstants.ApplicationScheme);
                var cookie = cookieOptions.TicketDataFormat.Protect(new AuthenticationTicket(new ClaimsPrincipal(claims), IdentityConstants.ApplicationScheme));
                securityApi.DefaultRequestHeaders.Add("Cookie", $"{cookieOptions.Cookie.Name}={cookie}");
                foreach (var path in new[] { "/api/v1/admin/intelligence/sources", "/api/v1/agent-management/inventory-settings/platform",
                    $"/api/v1/agent-management/devices/{identity.DeviceId:D}/integrity-scopes" })
                {
                    using var adminResponse = await securityApi.GetAsync(path, token);
                    Assert.AreEqual(HttpStatusCode.OK, adminResponse.StatusCode, path);
                }
                if (temporalAddress is null)
                {
                    using var offlineSigma = await securityApi.GetAsync("/api/v1/admin/sigma/rules", token);
                    Assert.AreEqual(HttpStatusCode.ServiceUnavailable, offlineSigma.StatusCode);
                }
                using (var missingCsrf = await securityApi.PutAsJsonAsync("/api/v1/agent-management/inventory-settings/platform",
                    new InventorySettingsUpdate(new InventorySettingsOverrides(300), ""), token))
                    Assert.AreEqual(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
                var policyPath = $"/api/v1/agent-management/devices/{identity.DeviceId:D}/integrity-scopes/";
                var policy = new IntegrityScopeInput(IntegrityScopeKind.Directory, "/srv/api-test", true,
                    ImmutableArray<string>.Empty, 3600, 1000, 1048576, 10485760, 16);
                using (var missingCsrf = await securityApi.PostAsJsonAsync(policyPath, policy, token))
                    Assert.AreEqual(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
                var adminPrincipal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, sessionHandler.AccountId.ToString("D")),
                    new Claim(ClaimTypes.Role, "Administrator")], IdentityConstants.ApplicationScheme));
                var antiContext = new DefaultHttpContext { RequestServices = security.Services, User = adminPrincipal };
                antiContext.Request.Scheme = "https";
                var anti = security.Services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(antiContext);
                var antiCookie = antiContext.Response.Headers.SetCookie.Single()!.Split(';')[0];
                securityApi.DefaultRequestHeaders.Remove("Cookie");
                securityApi.DefaultRequestHeaders.Add("Cookie", $"{cookieOptions.Cookie.Name}={cookie}; {antiCookie}");
                securityApi.DefaultRequestHeaders.Add("RequestVerificationToken", anti.RequestToken);
                using (var created = await securityApi.PostAsJsonAsync(policyPath, policy, token))
                {
                    Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
                    Assert.IsNotNull(await created.Content.ReadFromJsonAsync<IntegrityScopeView>(cancellationToken: token));
                }
                securityApi.DefaultRequestHeaders.Remove("RequestVerificationToken");
                sessionHandler.Role = "Read only";
                using (var denied = await securityApi.GetAsync(policyPath, token))
                    Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
                using (var readerInventory = await securityApi.GetAsync($"/api/v1/devices/{identity.DeviceId:D}/inventory", token))
                {
                    Assert.AreEqual(HttpStatusCode.OK, readerInventory.StatusCode);
                    Assert.IsTrue(readerInventory.Headers.CacheControl!.NoStore);
                }
                using (var readerVulnerabilities = await securityApi.GetAsync($"/api/v1/devices/{identity.DeviceId:D}/vulnerabilities", token))
                {
                    Assert.AreEqual(HttpStatusCode.OK, readerVulnerabilities.StatusCode);
                    Assert.IsTrue(readerVulnerabilities.Headers.CacheControl!.NoStore);
                }
                var readerChanges = await securityApi.GetFromJsonAsync<InventoryChangePage>($"/api/v1/devices/{identity.DeviceId:D}/inventory/changes?from={start}&until={end}", token);
                Assert.IsTrue(readerChanges!.Items.Any(item => item.After?.Name == "Private software"));
                var sigmaContent = await securityApi.GetStringAsync($"/api/v1/devices/{identity.DeviceId:D}/sigma-detections?from={start}&until={end}", token);
                Assert.Contains("private-detection.yml", sigmaContent);
                var readerProcess = await securityApi.GetFromJsonAsync<InventorySnapshotChunkView>($"/api/v1/devices/{identity.DeviceId:D}/inventory/{(int)process.Source}/snapshots/{process.SnapshotId:D}/chunks/0", token);
                Assert.AreEqual(42U, readerProcess!.Items.Single().NumericId);
                using (var unknown = await securityApi.GetAsync($"/api/v1/devices/{Guid.CreateVersion7():D}/vulnerabilities", token))
                    Assert.AreEqual(HttpStatusCode.NotFound, unknown.StatusCode);
                using (var deniedAdmin = await securityApi.GetAsync("/api/v1/admin/intelligence/sources", token))
                    Assert.AreEqual(HttpStatusCode.Forbidden, deniedAdmin.StatusCode);
                foreach (var invalidQuery in new[] { "page=0", "state=9" })
                {
                    using var invalid = await securityApi.GetAsync($"/api/v1/devices/{identity.DeviceId:D}/vulnerabilities?{invalidQuery}", token);
                    Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
                }
                securityApi.DefaultRequestHeaders.Remove("Cookie");
                securityApi.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "vlg_valid");
                using var missing = await securityApi.GetAsync($"/api/v1/devices/{Guid.CreateVersion7():D}/inventory", token);
                Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
            }

            var scopeId = Guid.CreateVersion7();
            var scope = new IntegrityScopeInput(IntegrityScopeKind.Directory, "/srv/app", true,
                ImmutableArray<string>.Empty, 3600, 1000, 1048576, 10485760, 16).WithIdentity(scopeId, 1);
            await using (var db = new SecurityAnalysisDbContext(securityOptions))
            {
                db.IntegrityScopes.Add(new IntegrityScopeSetting { Id = scopeId, DeviceId = identity.DeviceId, Revision = 1,
                    DefinitionJson = JsonSerializer.Serialize(scope), UpdatedBy = Guid.CreateVersion7(), UpdatedAt = nowUtc });
                await db.SaveChangesAsync(token);
            }
            var item = new IntegrityItem(IntegrityKey.Create(IntegrityItemKind.File, ["test"]), IntegrityItemKind.File, new string('a', 64));
            var measuredIntegrity = IntegrityDigest.MeasureChunk([item]);
            var integrity = new IntegrityChunk(IntegrityChunkCodec.SchemaVersion, Guid.CreateVersion7(), nowUtc, scopeId, 1,
                Guid.CreateVersion7(), 1, nowUtc, IntegrityScanOutcome.Complete, false, null, 0, 1, 1,
                measuredIntegrity.Bytes, IntegrityDigest.Combine([measuredIntegrity.Digest]), [item]);
            var integrityPayload = IntegrityChunkCodec.Serialize(integrity);
            using (var accepted = await SendAsync(client, integrityPayload, "valid", token)) Assert.AreEqual(HttpStatusCode.NoContent, accepted.StatusCode);
            using (var duplicate = await SendAsync(client, integrityPayload, "valid", token)) Assert.AreEqual(HttpStatusCode.NoContent, duplicate.StatusCode);
            if (temporalAddress is null)
            {
                var reference = writer.Batches.Single(x => x.BatchId == integrity.BatchId);
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Security"] = securityDb.ConnectionString }).Build();
                var worker = new SecurityHost::Vigiloom.Security.SecurityWorkHandler(config, new SingleHttpClientFactory(hostClient));
                var work = new ServiceWork(nameof(HostSecurityBatchReady), JsonSerializer.Serialize(reference), Guid.CreateVersion7().ToString());
                await worker.ExecuteAsync(work, token);
                await worker.ExecuteAsync(work, token);
            }
            else
            {
                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (true)
                {
                    await using var db = new SecurityAnalysisDbContext(securityOptions);
                    await using var hostState = new HostMonitoringDbContext(hostOptions);
                    if (await db.IntegrityScans.AnyAsync(x => x.ScopeId == scopeId && x.Status == IntegrityScanStatus.Complete, token) &&
                        !await hostState.HostSecurityBatches.AnyAsync(x => x.BatchId == integrity.BatchId, token)) break;
                    if (DateTime.UtcNow >= deadline) Assert.Fail("Temporal did not deliver the integrity batch.");
                    await Task.Delay(250, token);
                }
            }
            await using (var db = new SecurityAnalysisDbContext(securityOptions))
            {
                Assert.AreEqual(1, await db.IntegrityScans.CountAsync(x => x.ScopeId == scopeId && x.Status == IntegrityScanStatus.Complete, token));
                Assert.AreEqual(1L, (await db.IntegrityScopeStates.SingleAsync(x => x.ScopeId == scopeId, token)).CurrentGeneration);
            }
            await using (var db = new HostMonitoringDbContext(hostOptions)) Assert.IsFalse(await db.HostSecurityBatches.AnyAsync(x => x.BatchId == integrity.BatchId, token));

            using (var securityApi = security.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") }))
            {
                securityApi.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "vlg_valid");
                var path = $"/api/v1/devices/{identity.DeviceId:D}/integrity";
                var view = await securityApi.GetFromJsonAsync<DeviceIntegrityView>(path, token);
                Assert.AreEqual(integrity.SnapshotId, view!.Scopes.Single(x => x.ScopeId == scopeId).SnapshotId);
                var chunkPath = $"{path}/scopes/{scopeId:D}/snapshots/{integrity.SnapshotId:D}/chunks/0";
                var savedChunk = await securityApi.GetFromJsonAsync<IntegritySnapshotChunkView>(chunkPath, token);
                Assert.AreEqual(item.Key, savedChunk!.Items.Single().Key);
                using var wrongDevice = await securityApi.GetAsync(chunkPath.Replace(identity.DeviceId.ToString("D"), Guid.CreateVersion7().ToString("D")), token);
                Assert.AreEqual(HttpStatusCode.NotFound, wrongDevice.StatusCode);
                securityApi.DefaultRequestHeaders.Authorization = null;
                sessionHandler.Role = "Read only";
                var cookieOptions = security.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
                var claims = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, sessionHandler.AccountId.ToString("D")),
                    new Claim("AspNet.Identity.SecurityStamp", "stamp")], IdentityConstants.ApplicationScheme);
                var cookie = cookieOptions.TicketDataFormat.Protect(new AuthenticationTicket(new ClaimsPrincipal(claims), IdentityConstants.ApplicationScheme));
                securityApi.DefaultRequestHeaders.Add("Cookie", $"{cookieOptions.Cookie.Name}={cookie}");
                var readerView = await securityApi.GetFromJsonAsync<DeviceIntegrityView>(path, token);
                Assert.AreEqual(integrity.SnapshotId, readerView!.Scopes.Single(x => x.ScopeId == scopeId).SnapshotId);
                var readerChunk = await securityApi.GetFromJsonAsync<IntegritySnapshotChunkView>(chunkPath, token);
                Assert.AreEqual(item.Key, readerChunk!.Items.Single().Key);
            }

            var scanId = Guid.CreateVersion7();
            await using (var db = new SecurityAnalysisDbContext(securityOptions))
            {
                db.YaraScans.Add(new Vigiloom.Modules.SecurityAnalysis.Domain.YaraScan
                {
                    Id = scanId, AgentId = identity.AgentId, DeviceId = identity.DeviceId, RequestedBy = Guid.CreateVersion7(),
                    FilePath = "/tmp/test", Rules = "rule test { condition: true }", MaximumBytes = 1024,
                    TimeoutSeconds = 30, RequestedAt = nowUtc
                });
                await db.SaveChangesAsync(token);
            }
            var started = new YaraStartBatch(YaraStartBatchCodec.SchemaVersion, Guid.CreateVersion7(), nowUtc, scanId);
            var startPayload = YaraStartBatchCodec.Serialize(started);
            using (var accepted = await SendAsync(client, startPayload, "valid", token)) Assert.AreEqual(HttpStatusCode.NoContent, accepted.StatusCode);
            using (var duplicate = await SendAsync(client, startPayload, "valid", token)) Assert.AreEqual(HttpStatusCode.NoContent, duplicate.StatusCode);
            if (temporalAddress is null)
            {
                var start = Assert.ContainsSingle(writer.Starts);
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Security"] = securityDb.ConnectionString }).Build();
                var worker = new SecurityHost::Vigiloom.Security.SecurityWorkHandler(config, new SingleHttpClientFactory(hostClient));
                var work = new ServiceWork(nameof(YaraScanStarted), JsonSerializer.Serialize(start), Guid.CreateVersion7().ToString());
                await worker.ExecuteAsync(work, token);
            }
            else
            {
                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (true)
                {
                    await using var db = new SecurityAnalysisDbContext(securityOptions);
                    if (await db.YaraScans.AnyAsync(x => x.Id == scanId && x.StartedAt != null, token)) break;
                    if (DateTime.UtcNow >= deadline) Assert.Fail("Temporal did not deliver the YARA start.");
                    await Task.Delay(250, token);
                }
            }
            await using (var db = new SecurityAnalysisDbContext(securityOptions))
                Assert.AreEqual(nowUtc, (await db.YaraScans.SingleAsync(x => x.Id == scanId, token)).StartedAt);
        }, outerToken), context.CancellationToken);

    private sealed class SingleHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DeferredHostHandler(Func<HttpClient> client) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var target = client();
            using var forwarded = new HttpRequestMessage(request.Method, new Uri(target.BaseAddress!, request.RequestUri!.PathAndQuery));
            return await target.SendAsync(forwarded, cancellationToken);
        }
    }
    [TestMethod]
    public Task AuthenticatesAndRetriesAfterOperationsCommitted() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (hostDb, outerToken) =>
        await SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (operationsDb, token) =>
        {
            var hostOptions = new DbContextOptionsBuilder<HostMonitoringDbContext>().UseSqlServer(hostDb.ConnectionString,
                sql => sql.MigrationsHistoryTable(HostMonitoringDbContext.MigrationHistory, "agent")).Options;
            await using (var db = new HostMonitoringDbContext(hostOptions))
            {
                await db.Database.MigrateAsync(token);
                var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
                var policy = new AgentReplayPolicy();
                policy.InitializeFresh(now);
                policy.BeginCleanupCutover(now);
                db.ReplayPolicies.Add(policy);
                db.Set<AgentSetting>().Add(new AgentSetting { HeartbeatSeconds = 120, UpdatedBy = Guid.CreateVersion7(), UpdatedAt = now });
                await db.SaveChangesAsync(token);
            }
            var services = new ServiceCollection();
            services.AddModuleSqlServer<OperationsDbContext>(operationsDb.ConnectionString, "operations");
            services.AddModuleSqlServer<LogAdmissionDbContext>(operationsDb.ConnectionString, "logquota");
            services.AddModuleSqlServer<CentralLogsDbContext>(operationsDb.ConnectionString, "logs", migrationHistory: "__EFMigrationsHistory_CentralLogs");
            services.AddModuleSqlServer<SchedulingDbContext>(operationsDb.ConnectionString, "scheduling");
            using var provider = services.BuildServiceProvider();
            await MigrateAsync(provider.GetRequiredService<IDbContextFactory<OperationsDbContext>>(), token);
            await MigrateAsync(provider.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>(), token);
            await MigrateAsync(provider.GetRequiredService<IDbContextFactory<CentralLogsDbContext>>(), token);
            await MigrateAsync(provider.GetRequiredService<IDbContextFactory<SchedulingDbContext>>(), token);
            var identity = new RegisteredAgentIdentity(Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
            var operationSession = new AdminSessionHandler { Role = "Read only" };
            var operationRetention = new RetentionHandler();
            using var operations = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
            {
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:Operations"] = operationsDb.ConnectionString }));
                web.ConfigureTestServices(configured =>
                {
                    HostWorkers.RemoveBackgroundWork(configured);
                    configured.RemoveAll<IOutboxHandler>();
                    configured.AddScoped<IOutboxHandler, TestOutboxHandler>();
                    configured.AddHttpClient("Resources").ConfigurePrimaryHttpMessageHandler(() => new IdentityHandler(identity));
                    configured.AddHttpClient("Observability").ConfigurePrimaryHttpMessageHandler(() => operationRetention);
                    configured.AddHttpClient<IdentityCookieValidator>().ConfigurePrimaryHttpMessageHandler(() => operationSession);
                    configured.RemoveAll<IApiCredentialValidation>();
                    configured.AddSingleton<IApiCredentialValidation, CredentialValidation>();
                });
            });
            using var operationsClient = operations.CreateClient();
            var targetUpdate = new RecordedAgentUpdateTarget(Guid.CreateVersion7(), identity.AgentId, Guid.CreateVersion7(), "1.2.3", "win-x64", new string('a', 64), DateTimeOffset.UtcNow);
            var sink = new CommitThenLoseResponse(operationsClient);
            var securityWriter = new SecurityWriter();
            using var host = new WebApplicationFactory<HostMonitoring::Program>().WithWebHostBuilder(web =>
            {
                web.UseSetting("Hangfire:Enabled", "false");
                web.UseSetting("Temporal:Enabled", "false");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:HostMonitoring"] = hostDb.ConnectionString }));
                web.ConfigureTestServices(configured =>
                {
                    configured.AddHttpClient("resources").ConfigurePrimaryHttpMessageHandler(() => new IdentityHandler(identity));
                    configured.AddHttpClient("operations").ConfigurePrimaryHttpMessageHandler(() => operations.Server.CreateHandler());
                    configured.RemoveAll<IRecordedAgentTargetReader>();
                    configured.AddSingleton<IRecordedAgentTargetReader>(new TargetReader(targetUpdate));
                    configured.RemoveAll<IHostLogSink>();
                    configured.AddSingleton<IHostLogSink>(sink);
                    configured.AddSingleton<IHostSecurityWorkWriter>(securityWriter);
                });
            });
            using var client = host.CreateClient();
            var inboxSettings = await client.GetFromJsonAsync<AgentInboxSettingsSnapshot>("/internal/v1/agent-inbox", token);
            Assert.IsTrue(inboxSettings!.CleanupReady);
            using (var preview = await client.PostAsJsonAsync("/internal/v1/agent-inbox/preview",
                new InboxPreviewCommand(new(43200, inboxSettings.RetentionDays, inboxSettings.Version), Guid.CreateVersion7()), token))
            {
                Assert.AreEqual(HttpStatusCode.OK, preview.StatusCode);
                Assert.AreEqual(43200, (await preview.Content.ReadFromJsonAsync<AgentInboxSettingsPreview>(cancellationToken: token))!.ReplaySeconds);
            }
            var retention = await client.GetFromJsonAsync<HostMetricRetentionSnapshot>("/internal/v1/host-metrics/retention", token);
            Assert.AreEqual(30, retention!.RawDays);
            using (var unauthorizedSettings = await client.GetAsync("/api/v1/agent-management/settings/platform", token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorizedSettings.StatusCode);
            using (var unauthorizedHeartbeat = await client.PostAsync("/api/v1/agent/heartbeat", null, token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorizedHeartbeat.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid");
            using (var settings = await client.GetAsync("/api/v1/agent/settings", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode);
                var snapshot = await settings.Content.ReadFromJsonAsync<AgentSettingsSnapshot>(cancellationToken: token);
                Assert.AreEqual(120, snapshot!.Settings.HeartbeatSeconds);
                using var unchangedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent/settings");
                unchangedRequest.Headers.IfNoneMatch.Add(settings.Headers.ETag!);
                using var unchanged = await client.SendAsync(unchangedRequest, token);
                Assert.AreEqual(HttpStatusCode.NotModified, unchanged.StatusCode);
            }
            var epoch = Guid.CreateVersion7();
            using (var session = await client.PostAsJsonAsync("/api/v1/agent/queue-session", new AgentQueueSessionRequest(null, epoch), token))
                Assert.AreEqual(HttpStatusCode.NoContent, session.StatusCode);
            using (var heartbeat = await client.PostAsJsonAsync("/api/v1/agent/heartbeat", new AgentHeartbeatReport(1, epoch, 1, null), token))
                Assert.AreEqual(HttpStatusCode.NoContent, heartbeat.StatusCode);
            var update = new AgentUpdateResult(Guid.CreateVersion7(), targetUpdate.UpdateId, targetUpdate.PackageId, targetUpdate.ManifestSha256, 1,
                AgentUpdateResultKind.Succeeded, DateTimeOffset.UtcNow, false, null);
            using (var result = await client.PostAsJsonAsync("/api/v1/agent/update-results", update, token))
                Assert.AreEqual(HttpStatusCode.NoContent, result.StatusCode);
            var updateStatus = await client.GetFromJsonAsync<AgentVersionObservation>($"/internal/v1/agents/{identity.AgentId:D}/update-status", token);
            Assert.AreEqual("1.2.3", updateStatus!.LatestResult!.PackageVersion);
            Assert.AreEqual(epoch, (await client.GetFromJsonAsync<AgentQueueSession>("/api/v1/agent/queue-session", token))!.Epoch);
            var capacityCommand = new AgentQueueCapacityCommand(new AgentQueueCapacityUpdate(new AgentQueueCapacityOverrides(128L * 1024 * 1024, null), ""), Guid.CreateVersion7());
            using (var updated = await client.PostAsJsonAsync($"/internal/v1/agent-queue-capacity/devices/{identity.DeviceId:D}", capacityCommand, token))
                Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
            var capacity = await client.GetFromJsonAsync<AgentQueueCapacitySnapshot>("/api/v1/agent/queue-capacity", token);
            Assert.AreEqual(128L * 1024 * 1024, capacity!.Capacity.PayloadBytes);
            var metricCommand = new AgentMetricSettingsCommand(new AgentMetricSettingsUpdate(new AgentMetricSettingsOverrides(45, null), ""), Guid.CreateVersion7());
            using (var updated = await client.PostAsJsonAsync($"/internal/v1/agent-metric-settings/devices/{identity.DeviceId:D}", metricCommand, token))
                Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
            using (var metricResponse = await client.GetAsync("/api/v1/agent/metric-settings", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, metricResponse.StatusCode);
                var snapshot = await metricResponse.Content.ReadFromJsonAsync<AgentMetricSettingsSnapshot>(cancellationToken: token);
                Assert.AreEqual(45, snapshot!.Settings.HostIntervalSeconds);
                using var unchangedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent/metric-settings");
                unchangedRequest.Headers.IfNoneMatch.Add(metricResponse.Headers.ETag!);
                using var unchanged = await client.SendAsync(unchangedRequest, token);
                Assert.AreEqual(HttpStatusCode.NotModified, unchanged.StatusCode);
            }
            var logSourceCommand = new HostLogSourceCreateCommand(new(AgentLogSourceKind.Journal, "system", "local", true, null), Guid.CreateVersion7());
            HostLogSourceView savedLogSource;
            using (var created = await client.PostAsJsonAsync($"/internal/v1/devices/{identity.DeviceId:D}/log-sources", logSourceCommand, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, created.StatusCode);
                savedLogSource = (await created.Content.ReadFromJsonAsync<HostLogConfigurationResult>(cancellationToken: token))!.Source!;
            }
            string logRevision;
            using (var settings = await client.GetAsync("/api/v1/agent/log-settings", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode);
                var snapshot = await settings.Content.ReadFromJsonAsync<AgentLogSettingsSnapshot>(cancellationToken: token);
                Assert.AreEqual(savedLogSource.Settings.SourceId, Assert.ContainsSingle(snapshot!.Sources).SourceId);
                logRevision = snapshot.Revision;
                using var unchangedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent/log-settings");
                unchangedRequest.Headers.IfNoneMatch.Add(settings.Headers.ETag!);
                using var unchanged = await client.SendAsync(unchangedRequest, token);
                Assert.AreEqual(HttpStatusCode.NotModified, unchanged.StatusCode);
            }
            var queue = new AgentQueueSnapshot(0, 0, 0, capacity.Capacity.PayloadBytes, capacity.Capacity.BatchCount);
            var capacityReport = new AgentQueueCapacityReport(capacity, DateTimeOffset.UtcNow, null);
            using (var heartbeat = await client.PostAsJsonAsync("/api/v1/agent/heartbeat", new AgentHeartbeatReport(1, epoch, 2, queue, CapacityConfiguration: capacityReport, LogStatus: new(logRevision, false, []), InventoryConfiguration: new(null, null, null), IntegrityConfiguration: new(null, null, null, null), BaselineConfiguration: new(null, null, DateTimeOffset.UtcNow, null), Runtime: new("1.2.3", "win-x64")), token))
                Assert.AreEqual(HttpStatusCode.NoContent, heartbeat.StatusCode);
            var logStatus = await client.GetFromJsonAsync<HostLogStatusView>($"/internal/v1/devices/{identity.DeviceId:D}/agent-log-status", token);
            Assert.AreEqual(logRevision, logStatus!.Report.Revision);
            var securityStatus = await client.GetFromJsonAsync<SecurityObservation>($"/internal/v1/devices/{identity.DeviceId:D}/agent-security-observation", token);
            Assert.AreEqual(identity.AgentId, securityStatus!.Agent!.Id);
            Assert.AreEqual(epoch, securityStatus.Agent.InventoryConfigurationEpoch);
            Assert.AreEqual(epoch, securityStatus.Agent.IntegrityConfigurationEpoch);
            Assert.IsNotNull(securityStatus.Agent.BaselineConfigurationJson);
            using (var usage = await client.PostAsJsonAsync("/internal/v1/agent-package-usage", new AgentPackageUsageRequest("1.2.3", "win-x64"), token))
                Assert.IsTrue((await usage.Content.ReadFromJsonAsync<AgentPackageUsageResponse>(cancellationToken: token))!.InUse);
            using (var usage = await client.PostAsJsonAsync("/internal/v1/agent-package-usage", new AgentPackageUsageRequest("9.9.9", "win-x64"), token))
                Assert.IsFalse((await usage.Content.ReadFromJsonAsync<AgentPackageUsageResponse>(cancellationToken: token))!.InUse);
            var command = new AgentSettingsCommand(new AgentSettingsUpdate(new AgentSettingsOverrides(90, null, null), ""), Guid.CreateVersion7());
            using (var updated = await client.PostAsJsonAsync($"/internal/v1/agent-settings/devices/{identity.DeviceId:D}", command, token))
                Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
            using (var effective = await client.GetAsync("/api/v1/agent/settings", token))
                Assert.AreEqual(90, (await effective.Content.ReadFromJsonAsync<AgentSettingsSnapshot>(cancellationToken: token))!.Settings.HeartbeatSeconds);
            client.DefaultRequestHeaders.Authorization = null;
            var nowUtc = DateTimeOffset.UtcNow;
            var metric = new AgentMetricPoint(Guid.CreateVersion7(), nowUtc, AgentMetricKind.CpuTotalPercent, "cpu", null, 25);
            var metricBatch = new AgentMetricBatch(AgentMetricBatchCodec.SchemaVersion, Guid.CreateVersion7(), nowUtc, [metric]);
            var metricPayload = AgentMetricBatchCodec.Serialize(metricBatch);
            using (var compressed = new MemoryStream())
            {
                using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true)) await gzip.WriteAsync(metricPayload, token);
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/batches") { Content = new ByteArrayContent(compressed.ToArray()) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Content.Headers.ContentEncoding.Add("gzip");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "valid");
                using var response = await client.SendAsync(request, token);
                Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
            }
            var from = Uri.EscapeDataString(nowUtc.AddMinutes(-1).ToString("O"));
            var until = Uri.EscapeDataString(nowUtc.AddMinutes(1).ToString("O"));
            var metrics = await client.GetFromJsonAsync<HostMetricPage>($"/internal/v1/devices/{identity.DeviceId:D}/host-metrics?from={from}&until={until}", token);
            Assert.AreEqual(25d, Assert.ContainsSingle(metrics!.Items).Value);
            var yaraId = Guid.CreateVersion7();
            var yara = YaraResultBatchCodec.Serialize(new(YaraResultBatchCodec.SchemaVersion, yaraId, nowUtc,
                new(YaraScanOutcome.Completed, "match", "", false, nowUtc)));
            using (var yaraResponse = await SendAsync(client, yara, "valid", token))
                Assert.AreEqual(HttpStatusCode.NoContent, yaraResponse.StatusCode);
            using (var duplicateYara = await SendAsync(client, yara, "valid", token))
                Assert.AreEqual(HttpStatusCode.NoContent, duplicateYara.StatusCode);
            Assert.AreEqual("yara", Assert.ContainsSingle(securityWriter.Batches).Kind);
            await using (var verify = new HostMonitoringDbContext(hostOptions))
                CollectionAssert.AreEqual(yara, (await verify.HostSecurityBatches.SingleAsync(x => x.BatchId == yaraId, token)).Payload);
            var series = new HostMetricThresholdQuery(identity.DeviceId, identity.AgentId, AgentMetricKind.CpuTotalPercent, "cpu", null);
            var thresholdCommand = new HostMetricThresholdCommand(series, new(true, HostMetricComparison.AtLeast, 20, 120, ""), Guid.CreateVersion7());
            using (var savedThreshold = await client.PostAsJsonAsync("/internal/v1/host-threshold/update", thresholdCommand, token))
                Assert.AreEqual(HttpStatusCode.OK, savedThreshold.StatusCode);
            using (var evaluated = await client.PostAsJsonAsync("/internal/v1/host-threshold/query", series, token))
                Assert.AreEqual(HostMetricThresholdState.Exceeded, (await evaluated.Content.ReadFromJsonAsync<HostMetricThresholdStatus>(cancellationToken: token))!.State);
            var auditQuery = new OperationAuditQuery(nowUtc.AddMinutes(-5), nowUtc.AddMinutes(5));
            foreach (var (source, action) in new[]
            {
                (AuditFeedSource.AgentSettings, AuditFeedAction.AgentSettingsChanged),
                (AuditFeedSource.AgentQueueCapacity, AuditFeedAction.AgentQueueCapacityChanged),
                (AuditFeedSource.AgentMetricSettings, AuditFeedAction.AgentMetricSettingsChanged),
                (AuditFeedSource.HostMetricThresholds, AuditFeedAction.HostMetricThresholdChanged),
                (AuditFeedSource.HostLogSources, AuditFeedAction.HostLogSourceCreated)
            })
            {
                using var auditResponse = await client.PostAsJsonAsync("/internal/v1/audit/query", new AuditFeedRequest(source, auditQuery), token);
                Assert.AreEqual(HttpStatusCode.OK, auditResponse.StatusCode);
                var audit = await auditResponse.Content.ReadFromJsonAsync<AuditFeedPage>(cancellationToken: token);
                Assert.IsTrue(audit!.Items.Any(x => x.Action == action && x.SubjectId == identity.DeviceId));
            }
            var item = new AgentLogRecord(Guid.CreateVersion7(), nowUtc, "host event", null, null, null, null, null);
            var batch = new AgentLogBatch(AgentLogBatchCodec.SchemaVersion, Guid.CreateVersion7(), nowUtc,
                Guid.CreateVersion7(), AgentLogSourceKind.Journal, "System", 1, [item]);
            var payload = AgentLogBatchCodec.Serialize(batch);
            var beforePending = await client.GetFromJsonAsync<AgentReceiverSnapshot>("/internal/v1/agent-receiver/status", token);
            Assert.IsNotNull(beforePending!.LastCommittedBatchAt);
            using (var unauthorized = await SendAsync(client, payload, null, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
            using (var trailingSlash = await client.PostAsync("/api/v1/agent/batches/", new ByteArrayContent(payload), token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, trailingSlash.StatusCode);
            using (var lost = await SendAsync(client, payload, "valid", token)) Assert.AreEqual(HttpStatusCode.ServiceUnavailable, lost.StatusCode);
            var whilePending = await client.GetFromJsonAsync<AgentReceiverSnapshot>("/internal/v1/agent-receiver/status", token);
            Assert.AreEqual(beforePending.LastCommittedBatchAt, whilePending!.LastCommittedBatchAt);
            using (var repeated = await SendAsync(client, payload, "valid", token)) Assert.AreEqual(HttpStatusCode.NoContent, repeated.StatusCode);
            var afterDelivery = await client.GetFromJsonAsync<AgentReceiverSnapshot>("/internal/v1/agent-receiver/status", token);
            Assert.IsTrue(afterDelivery!.LastCommittedBatchAt >= beforePending.LastCommittedBatchAt);
            using (var already = await SendAsync(client, payload, "valid", token)) Assert.AreEqual(HttpStatusCode.NoContent, already.StatusCode);
            await using var logs = await provider.GetRequiredService<IDbContextFactory<CentralLogsDbContext>>().CreateDbContextAsync(token);
            Assert.AreEqual(1, await logs.HostLogs.CountAsync(token));
            await using var auditDb = await provider.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token);
            var auditCount = await auditDb.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM operations.AuditEvents").SingleAsync(token);
            using (var publicClient = operations.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") }))
            {
                using (var denied = await publicClient.GetAsync($"/api/v1/devices/{identity.DeviceId:D}/host-logs?from={from}&until={until}", token))
                    Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
                publicClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid");
                using (var deniedAgent = await publicClient.GetAsync($"/api/v1/devices/{identity.DeviceId:D}/host-logs?from={from}&until={until}", token))
                    Assert.AreEqual(HttpStatusCode.Unauthorized, deniedAgent.StatusCode);
                publicClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "vlg_valid");
                var page = await publicClient.GetFromJsonAsync<HostLogPage>($"/api/v1/devices/{identity.DeviceId:D}/host-logs?from={from}&until={until}", token);
                Assert.IsNotNull(page);
                Assert.HasCount(1, page.Items);
                Assert.AreEqual(item.Content, page.Items[0].Content);
                Assert.AreEqual(14, page.RetentionDays);
                publicClient.DefaultRequestHeaders.Authorization = null;
                var operationsCookieOptions = operations.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
                var claims = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, operationSession.AccountId.ToString("D")),
                    new Claim("AspNet.Identity.SecurityStamp", "stamp")], IdentityConstants.ApplicationScheme);
                var cookie = operationsCookieOptions.TicketDataFormat.Protect(new AuthenticationTicket(new ClaimsPrincipal(claims), IdentityConstants.ApplicationScheme));
                publicClient.DefaultRequestHeaders.Add("Cookie", $"{operationsCookieOptions.Cookie.Name}={cookie}");
                operationRetention.Days = 10;
                using (var reader = await publicClient.GetAsync($"/api/v1/devices/{identity.DeviceId:D}/host-logs?from={from}&until={until}", token))
                {
                    Assert.AreEqual(HttpStatusCode.OK, reader.StatusCode);
                    Assert.IsTrue(reader.Headers.CacheControl!.NoStore);
                    var history = await reader.Content.ReadFromJsonAsync<HostLogPage>(cancellationToken: token);
                    Assert.AreEqual(10, history!.RetentionDays);
                    Assert.AreEqual(item.Content, Assert.ContainsSingle(history.Items).Content);
                }
                using var invalid = await publicClient.GetAsync($"/api/v1/devices/{Guid.CreateVersion7():D}/host-logs?from={until}&until={from}", token);
                Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            }
            Assert.AreEqual(auditCount + 2, await auditDb.Database.SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM operations.AuditEvents").SingleAsync(token));
            await using var check = new HostMonitoringDbContext(hostOptions);
            Assert.AreEqual(120, (await check.AgentStatuses.SingleAsync(token)).HeartbeatSafetyIntervalSeconds);
            Assert.IsFalse(await check.ReceivedBatches.AnyAsync(x => EF.Property<bool>(x, "PendingDelivery"), token));
            Assert.AreEqual(1, await check.HostMetrics.CountAsync(token));
            Assert.AreEqual(2, sink.Calls);

            var thresholdSession = new AdminSessionHandler();
            using var proxiedHost = new WebApplicationFactory<HostMonitoring::Program>().WithWebHostBuilder(web =>
            {
                web.UseUrls("http://127.0.0.1:0");
                web.UseSetting("Hangfire:Enabled", "false");
                web.UseSetting("Temporal:Enabled", "false");
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:HostMonitoring"] = hostDb.ConnectionString,
                    ["Vigiloom:Gateway:TrustedProxies:0"] = "127.0.0.1"
                }));
                web.ConfigureTestServices(configured =>
                {
                    configured.AddHttpClient("resources").ConfigurePrimaryHttpMessageHandler(() => new IdentityHandler(identity));
                    configured.AddHttpClient<IdentityCookieValidator>().ConfigurePrimaryHttpMessageHandler(() => thresholdSession);
                    configured.AddHttpClient("security").ConfigurePrimaryHttpMessageHandler(() => new IntegritySettingsHandler());
                    configured.RemoveAll<IApiCredentialValidation>();
                    configured.AddSingleton<IApiCredentialValidation, CredentialValidation>();
                });
            });
            proxiedHost.UseKestrel(0);
            using var proxyClient = proxiedHost.CreateClient();
            proxyClient.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
            proxyClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "vlg_valid");
            using (var response = await proxyClient.GetAsync($"/api/v1/devices/{identity.DeviceId:D}/agent-settings", token))
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var status = await proxyClient.GetFromJsonAsync<AgentConnectionStatus>($"/api/v1/devices/{identity.DeviceId:D}/agent-status", token);
            Assert.AreEqual(AgentConnectionState.Online, status!.State);
            var settingsStatus = await proxyClient.GetFromJsonAsync<AgentSettingsStatus>($"/api/v1/devices/{identity.DeviceId:D}/agent-settings-status", token);
            Assert.AreEqual(AgentSettingsApplyState.WaitingForReport, settingsStatus!.State);
            var capacityStatus = await proxyClient.GetFromJsonAsync<AgentQueueCapacityStatus>($"/api/v1/devices/{identity.DeviceId:D}/agent-queue-capacity-status", token);
            Assert.AreEqual(AgentSettingsApplyState.Applied, capacityStatus!.State);
            using (var integrityResponse = await proxyClient.GetAsync($"/api/v1/devices/{identity.DeviceId:D}/integrity/settings-status", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, integrityResponse.StatusCode);
                Assert.IsTrue(integrityResponse.Headers.CacheControl?.NoStore);
                var integrity = await integrityResponse.Content.ReadFromJsonAsync<IntegritySettingsStatus>(cancellationToken: token);
                Assert.IsNotNull(integrity);
                Assert.AreEqual(identity.DeviceId, integrity.DeviceId);
                Assert.AreEqual(IntegritySettingsSnapshot.Create([], 0).Revision, integrity.DesiredRevision);
            }
            var publicMetrics = await proxyClient.GetFromJsonAsync<HostMetricPage>($"/api/v1/devices/{identity.DeviceId:D}/host-metrics?from={from}&until={until}", token);
            Assert.IsNotNull(publicMetrics);
            var publicThreshold = await proxyClient.GetFromJsonAsync<HostMetricThresholdStatus>($"/api/v1/devices/{identity.DeviceId:D}/host-metric-threshold?agentId={identity.AgentId:D}&kind=CpuTotalPercent&source=cpu", token);
            Assert.AreEqual(HostMetricThresholdState.Exceeded, publicThreshold!.State);
            var thresholdUrl = $"/api/v1/devices/{identity.DeviceId:D}/host-metric-threshold?agentId={identity.AgentId:D}&kind=CpuTotalPercent&source=cpu";
            var thresholdInput = new HostMetricThresholdInput(true, HostMetricComparison.AtLeast, 30, 120, publicThreshold.Threshold!.Version);
            proxyClient.DefaultRequestHeaders.Authorization = null;
            var cookieOptions = proxiedHost.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
            var adminIdentity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, thresholdSession.AccountId.ToString("D")),
                new Claim("AspNet.Identity.SecurityStamp", "stamp")], IdentityConstants.ApplicationScheme);
            var adminCookie = cookieOptions.TicketDataFormat.Protect(new AuthenticationTicket(new ClaimsPrincipal(adminIdentity), IdentityConstants.ApplicationScheme));
            proxyClient.DefaultRequestHeaders.Add("Cookie", $"{cookieOptions.Cookie.Name}={adminCookie}");
            using (var missingCsrf = await proxyClient.PutAsJsonAsync(thresholdUrl, thresholdInput, token))
                Assert.AreEqual(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
            var antiContext = new DefaultHttpContext { RequestServices = proxiedHost.Services, User = new ClaimsPrincipal(adminIdentity) };
            antiContext.Request.Scheme = "https";
            var anti = proxiedHost.Services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(antiContext);
            var antiCookie = antiContext.Response.Headers.SetCookie.Single()!.Split(';')[0];
            proxyClient.DefaultRequestHeaders.Remove("Cookie");
            proxyClient.DefaultRequestHeaders.Add("Cookie", $"{cookieOptions.Cookie.Name}={adminCookie}; {antiCookie}");
            proxyClient.DefaultRequestHeaders.Add("RequestVerificationToken", anti.RequestToken);
            using (var saved = await proxyClient.PutAsJsonAsync(thresholdUrl, thresholdInput, token))
                Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
            using (var stale = await proxyClient.PutAsJsonAsync(thresholdUrl, thresholdInput, token))
                Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
            var normal = await proxyClient.GetFromJsonAsync<HostMetricThresholdStatus>(thresholdUrl, token);
            Assert.AreEqual(HostMetricThresholdState.Normal, normal!.State);
            Assert.AreEqual(HostMetricThresholdState.NotConfigured,
                (await proxyClient.GetFromJsonAsync<HostMetricThresholdStatus>(thresholdUrl + "&dimension=other", token))!.State);
            Assert.AreEqual(HostMetricThresholdState.NotConfigured,
                (await proxyClient.GetFromJsonAsync<HostMetricThresholdStatus>(thresholdUrl.Replace("source=cpu", "source=cpu%20"), token))!.State);
            await using (var audit = new HostMonitoringDbContext(hostOptions))
                Assert.AreEqual(2, await audit.Set<HostMetricThresholdAudit>().CountAsync(token));
            thresholdSession.Role = "Read only";
            using (var readerUpdate = await proxyClient.PutAsJsonAsync(thresholdUrl, thresholdInput, token))
                Assert.AreEqual(HttpStatusCode.Forbidden, readerUpdate.StatusCode);
            Assert.AreEqual(HostMetricThresholdState.Normal, (await proxyClient.GetFromJsonAsync<HostMetricThresholdStatus>(thresholdUrl, token))!.State);
            thresholdSession.Role = "Administrator";
            await using (var db = new HostMonitoringDbContext(hostOptions))
            {
                foreach (var (dimension, observed) in new[] { ("stale", nowUtc.AddMinutes(-10)), ("future", nowUtc.AddMinutes(10)) })
                    db.HostMetrics.Add(new HostMetric(identity.AgentId, Guid.CreateVersion7(), 0,
                        new AgentMetricPoint(Guid.CreateVersion7(), observed, AgentMetricKind.CpuTotalPercent, "cpu", dimension, 95), nowUtc));
                await db.SaveChangesAsync(token);
            }
            foreach (var (dimension, reason) in new[] { ("stale", HostMetricUnknownReason.Stale), ("future", HostMetricUnknownReason.FutureTimestamp), ("missing", HostMetricUnknownReason.NoSample) })
            {
                var otherUrl = thresholdUrl + "&dimension=" + dimension;
                using var created = await proxyClient.PutAsJsonAsync(otherUrl, new HostMetricThresholdInput(true, HostMetricComparison.AtLeast, 80, 120, ""), token);
                Assert.AreEqual(HttpStatusCode.OK, created.StatusCode);
                var unknown = await proxyClient.GetFromJsonAsync<HostMetricThresholdStatus>(otherUrl, token);
                Assert.AreEqual(HostMetricThresholdState.Unknown, unknown!.State);
                Assert.AreEqual(reason, unknown.UnknownReason);
            }
            var concurrentInput = thresholdInput with { Version = normal.Threshold!.Version };
            var concurrent = await Task.WhenAll(proxyClient.PutAsJsonAsync(thresholdUrl, concurrentInput with { Value = 31 }, token),
                proxyClient.PutAsJsonAsync(thresholdUrl, concurrentInput with { Value = 32 }, token));
            try { CollectionAssert.AreEquivalent(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, concurrent.Select(x => x.StatusCode).ToArray()); }
            finally { foreach (var response in concurrent) response.Dispose(); }
            using (var auditResponse = await client.PostAsJsonAsync("/internal/v1/audit/query", new AuditFeedRequest(AuditFeedSource.HostMetricThresholds, auditQuery), token))
            {
                var feed = await auditResponse.Content.ReadFromJsonAsync<AuditFeedPage>(cancellationToken: token);
                var detail = feed!.Items.First(x => x.MetricThreshold?.ThresholdId == normal.Threshold.Id).MetricThreshold!;
                Assert.AreEqual(identity.AgentId, detail.AgentId);
                Assert.AreEqual((int)AgentMetricKind.CpuTotalPercent, detail.Kind);
                Assert.AreEqual("cpu", detail.Source);
                Assert.IsNull(detail.Dimension);
            }
            proxyClient.DefaultRequestHeaders.Remove("Cookie");
            proxyClient.DefaultRequestHeaders.Remove("RequestVerificationToken");
            proxyClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "vlg_valid");
            using (var missing = await proxyClient.GetAsync($"/api/v1/devices/{Guid.CreateVersion7():D}/host-metrics?from={from}&until={until}", token))
            {
                Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
                Assert.AreEqual("application/problem+json", missing.Content.Headers.ContentType?.MediaType);
            }
            using (var response = await proxyClient.PostAsJsonAsync("/internal/v1/agent-connections", new[] { identity.DeviceId }, token))
            {
                response.EnsureSuccessStatusCode();
                var connections = await response.Content.ReadFromJsonAsync<Dictionary<Guid, AgentConnectionStatus>>(cancellationToken: token);
                Assert.AreEqual(AgentConnectionState.Online, connections![identity.DeviceId].State);
            }
            using (var response = await proxyClient.GetAsync("/api/v1/agent-management/settings/platform", token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);

            await using (var channel = new HubConnectionBuilder().WithUrl(new Uri(proxyClient.BaseAddress!, "/api/v1/agent/channel"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.AccessTokenProvider = () => Task.FromResult<string?>("valid");
            }).Build())
            {
                await channel.StartAsync(token);
                var channelReport = new AgentHeartbeatReport(1, epoch, 3, queue, Runtime: new("2.0.0", "win-x64"));
                Assert.AreEqual(204, await channel.InvokeAsync<int>("Heartbeat", "valid", channelReport, token));
                await using var channelDb = new HostMonitoringDbContext(hostOptions);
                Assert.AreEqual(3L, (await channelDb.AgentStatuses.SingleAsync(token)).QueueReportSequence);
                Assert.AreEqual(401, await channel.InvokeAsync<int>("Heartbeat", "revoked", null, token));
            }

            var identityBuilder = WebApplication.CreateBuilder();
            identityBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            await using var identityService = identityBuilder.Build();
            identityService.MapGet("/api/v1/agent/identity", (HttpContext ctx) => ctx.Request.Headers.Authorization == "Bearer valid"
                ? Results.Ok(new { identity.AgentId, identity.DeviceId }) : Results.Unauthorized());
            await identityService.StartAsync(token);
            var identityAddress = identityService.Urls.Single();
            using var gateway = new WebApplicationFactory<GatewayHost::Program>().WithWebHostBuilder(web =>
                web.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ReverseProxy:Clusters:host-monitoring:Destinations:host-monitoring:Address"] = proxyClient.BaseAddress!.ToString(),
                    ["ReverseProxy:Routes:agent-batches:ClusterId"] = "host-monitoring",
                    ["Vigiloom:Gateway:AgentIdentityAddress"] = identityAddress
                })));
            using var gatewayClient = gateway.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
            var forwardedPoint = new AgentMetricPoint(Guid.CreateVersion7(), nowUtc, AgentMetricKind.CpuTotalPercent, "gateway", null, 33);
            var forwardedBatch = AgentMetricBatchCodec.Serialize(new(AgentMetricBatchCodec.SchemaVersion, Guid.CreateVersion7(), nowUtc, [forwardedPoint]));
            using (var forwarded = await SendAsync(gatewayClient, forwardedBatch, "valid", token)) Assert.AreEqual(HttpStatusCode.NoContent, forwarded.StatusCode);
            await using var verifyForwarded = new HostMonitoringDbContext(hostOptions);
            Assert.IsTrue(await verifyForwarded.HostMetrics.AnyAsync(x => x.MessageId == forwardedPoint.MessageId && x.Value == 33, token));
        }, outerToken), context.CancellationToken);

    private static async Task MigrateAsync<T>(IDbContextFactory<T> factory, CancellationToken token) where T : DbContext
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await db.Database.MigrateAsync(token);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, byte[] payload, string? credential, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/batches") { Content = new ByteArrayContent(payload) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (credential is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return await client.SendAsync(request, token);
    }

    private sealed class IdentityHandler(RegisteredAgentIdentity identity) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/agents/active", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { identity.AgentId }) };
            if (request.RequestUri!.AbsolutePath.EndsWith("/agent-overview", StringComparison.Ordinal))
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new DeviceAgentOverview(
                    new DeviceAgentState(identity.DeviceId, identity.DeviceGeneration, true),
                    new AgentRegistrationSummary(identity.AgentId, identity.DeviceGeneration, DateTimeOffset.UtcNow, null)) }) };
            if (request.Method == HttpMethod.Get)
            {
                if (request.RequestUri.AbsolutePath.StartsWith("/internal/v1/devices/", StringComparison.Ordinal) &&
                    !request.RequestUri.AbsolutePath.StartsWith($"/internal/v1/devices/{identity.DeviceId:D}/", StringComparison.OrdinalIgnoreCase))
                    return new(HttpStatusCode.NotFound);
                if (request.RequestUri.AbsolutePath.EndsWith("/agent-ownership", StringComparison.Ordinal))
                    return new(HttpStatusCode.OK) { Content = JsonContent.Create(new DeviceAgentOwnership(
                        new DeviceAgentState(identity.DeviceId, identity.DeviceGeneration, true),
                        new AgentRegistrationSummary(identity.AgentId, identity.DeviceGeneration, DateTimeOffset.UtcNow, null))) };
                var payload = request.RequestUri!.AbsolutePath.EndsWith("/agents", StringComparison.Ordinal)
                    ? JsonContent.Create(new DeviceAgentRegistrations([new(identity.AgentId, identity.DeviceGeneration, DateTimeOffset.UtcNow, null)], [], identity.DeviceGeneration))
                    : JsonContent.Create(new DeviceAgentState(identity.DeviceId, identity.DeviceGeneration, true));
                return new(HttpStatusCode.OK) { Content = payload };
            }
            var command = await request.Content!.ReadFromJsonAsync<AgentCredentialCommand>(cancellationToken: token);
            return command?.Credential == "valid" ? new(HttpStatusCode.OK) { Content = JsonContent.Create(identity) } : new(HttpStatusCode.Unauthorized);
        }
    }

    private sealed class IntegritySettingsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(IntegritySettingsSnapshot.Create([], 0)) });
    }

    private sealed class RetentionHandler(int days = 14) : HttpMessageHandler
    {
        public int Days { get; set; } = days;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new Vigiloom.Contracts.Services.TelemetryRetentionSnapshot(Days, 30, "test")) });
    }

    private sealed class AuditHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<Vigiloom.Contracts.Agent.Baseline.BaselineExceptionNote>()) });
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        }
    }

    private sealed class AdminSessionHandler : HttpMessageHandler
    {
        public string Role { get; set; } = "Administrator";
        public Guid AccountId { get; } = Guid.CreateVersion7();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new ValidatedCookieResponse([
                new(ClaimTypes.NameIdentifier, AccountId.ToString("D"), ClaimValueTypes.String, "LOCAL AUTHORITY", "LOCAL AUTHORITY"),
                new(ClaimTypes.Role, Role, ClaimValueTypes.String, "LOCAL AUTHORITY", "LOCAL AUTHORITY")
            ])) });
    }

    private sealed class CredentialValidation : IApiCredentialValidation
    {
        public Task<VerifiedApiCredentialResponse?> VerifyAsync(string value, CancellationToken token) =>
            Task.FromResult<VerifiedApiCredentialResponse?>(value == "valid" ? new(Guid.CreateVersion7(), "test", false) : null);
    }

    private sealed class TargetReader(RecordedAgentUpdateTarget target) : IRecordedAgentTargetReader
    {
        public Task<RecordedAgentUpdateTarget?> GetAsync(Guid agentId, Guid updateId, CancellationToken token) =>
            Task.FromResult<RecordedAgentUpdateTarget?>(target.AgentId == agentId && target.UpdateId == updateId ? target : null);
    }

    private sealed class SecurityWriter : IHostSecurityWorkWriter
    {
        public List<HostSecurityBatchReady> Batches { get; } = [];
        public List<YaraScanStarted> Starts { get; } = [];
        public Task WriteStartAsync(HostMonitoringDbContext db, YaraScanStarted started, CancellationToken token) { Starts.Add(started); return Task.CompletedTask; }
        public Task WriteAsync(HostMonitoringDbContext db, HostSecurityBatchReady batch, CancellationToken token)
        {
            Batches.Add(batch);
            return Task.CompletedTask;
        }
    }

    private sealed class CommitThenLoseResponse(HttpClient client) : IHostLogSink
    {
        public int Calls { get; private set; }
        public async Task<LogReceipt> ReceiveAsync(RegisteredAgentIdentity identity, AgentLogBatch batch, CancellationToken token)
        {
            Calls++;
            using var response = await client.PostAsJsonAsync("/internal/v1/host-logs",
                new HostLogSubmission(identity.AgentId, identity.DeviceId, identity.DeviceGeneration, batch), token);
            response.EnsureSuccessStatusCode();
            var receipt = await response.Content.ReadFromJsonAsync<LogReceipt>(cancellationToken: token);
            return Calls == 1 ? new(LogReceiptStatus.Busy) : receipt!;
        }
    }

    private sealed class TestOutboxHandler : IOutboxHandler
    {
        public string MessageType => TemporalOutboxHandler.Type;
        public Task HandleAsync(OutboxMessage message, SqlConnection connection, SqlTransaction transaction, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
