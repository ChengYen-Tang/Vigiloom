using System.Net;
using System.IO.Compression;
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
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Application;
using System.Data.Common;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Api;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AgentIdentityHttpTests
{
    [TestMethod]
    public void WebRuntimeConfiguration_RejectsTruncatedCompressedStreams()
    {
        using var runtime = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vigiloom.Web.runtimeconfig.json")));
        Assert.IsTrue(runtime.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties")
            .GetProperty("System.IO.Compression.UseStrictValidation").GetBoolean());
    }

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public Task AgentTransport_PreAuthenticationConcurrencyIsBoundedAndReleased() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var gate = new TestDeviceGate();
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Vigiloom"] = database.ConnectionString,
                    ["Vigiloom:Agent:Transport:MaxConcurrentRequests"] = "1",
                    ["Vigiloom:Agent:Transport:MaxQueuedRequests"] = "0"
                }));
                builder.ConfigureServices(services => services.AddScoped<IDeviceAgentSource>(provider =>
                    new BlockingDeviceSource(new DeviceAgentSource(provider.GetRequiredService<IDbContextFactory<ResourcesDbContext>>()), gate)));
            });
            var resources = factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
            await using (var db = await resources.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var agents = factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>();
            await using (var db = await agents.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var device = new Device("Agent", "127.0.0.1", true, DateTimeOffset.UtcNow);
            await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
            string credential;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var identity = scope.ServiceProvider.GetRequiredService<AgentIdentity>();
                var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
                var secret = RandomNumberGenerator.GetBytes(32);
                var registered = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
                credential = $"vla_{registered.AgentId:D}.{WebEncoders.Base64UrlEncode(secret)}";
            }
            var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };
            using var first = factory.CreateClient(options);
            using var second = factory.CreateClient(options);
            first.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            second.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            gate.Enabled = true;
            var pending = first.PostAsync("/api/v1/agent/heartbeat", null, token);
            HttpStatusCode? firstStatus = null;
            try
            {
                await gate.Entered.Task.WaitAsync(token);
                using var limited = await second.PostAsync("/api/v1/agent/heartbeat", null, token);
                Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
                Assert.IsNotNull(limited.Headers.RetryAfter);
            }
            finally
            {
                gate.Release.TrySetResult();
                using var completed = await pending;
                firstStatus = completed.StatusCode;
            }
            Assert.AreEqual(HttpStatusCode.NoContent, firstStatus);
            using (var resumed = await second.PostAsync("/api/v1/agent/heartbeat", null, token)) Assert.AreEqual(HttpStatusCode.NoContent, resumed.StatusCode);
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task AgentTransport_RateLimitsVerifiedAgentsIndependentlyBehindOneAddress() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Vigiloom"] = database.ConnectionString,
                    ["Vigiloom:Authentication:ApiRequestsPerMinute"] = "1",
                    ["Vigiloom:Agent:Transport:RequestsPerMinute"] = "2"
                }));
            });
            var resources = factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
            await using (var db = await resources.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var agents = factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>();
            await using (var db = await agents.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var credentials = new List<string>();
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var identity = scope.ServiceProvider.GetRequiredService<AgentIdentity>();
                for (var i = 0; i < 2; i++)
                {
                    var device = new Device($"Agent {i}", "127.0.0.1", true, DateTimeOffset.UtcNow);
                    await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
                    var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
                    var secret = RandomNumberGenerator.GetBytes(32);
                    var registered = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
                    credentials.Add($"vla_{registered.AgentId:D}.{WebEncoders.Base64UrlEncode(secret)}");
                }
            }
            var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };
            using var invalid = factory.CreateClient(options);
            for (var i = 0; i < 5; i++)
            {
                invalid.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"vla_{Guid.CreateVersion7():D}.{WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32))}");
                using var denied = await invalid.PostAsync("/api/v1/agent/heartbeat", null, token);
                Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            }
            using var first = factory.CreateClient(options);
            using var second = factory.CreateClient(options);
            first.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credentials[0]);
            second.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credentials[1]);
            using (var heartbeat = await first.PostAsync("/api/v1/agent/heartbeat", null, token)) Assert.AreEqual(HttpStatusCode.NoContent, heartbeat.StatusCode);
            var now = DateTimeOffset.UtcNow;
            var message = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, now, "Test OS", "X64", ".NET 10");
            using (var batch = await first.PostAsync("/api/v1/agent/batches", JsonBytes(AgentBatchCodec.Serialize(new AgentBatch(1, Guid.CreateVersion7(), now, [message]))), token))
                Assert.AreEqual(HttpStatusCode.NoContent, batch.StatusCode);
            using (var limited = await first.PostAsync("/api/v1/agent/heartbeat", null, token))
            {
                Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
                Assert.IsNotNull(limited.Headers.RetryAfter);
            }
            using (var heartbeat = await second.PostAsync("/api/v1/agent/heartbeat", null, token)) Assert.AreEqual(HttpStatusCode.NoContent, heartbeat.StatusCode);
            using (var heartbeat = await second.PostAsync("/api/v1/agent/heartbeat", null, token)) Assert.AreEqual(HttpStatusCode.NoContent, heartbeat.StatusCode);
        }, TestContext.CancellationToken);

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
                var reportAt = DateTimeOffset.UtcNow;
                var report = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, reportAt,
                    System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                    System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
                var batch = new AgentBatch(1, Guid.CreateVersion7(), reportAt, [report]);
                var bytes = AgentBatchCodec.Serialize(batch);
                var queue = new DurableBatchQueue(vault, recoveredId);
                Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(batch.BatchId, reportAt, 1, bytes, token));
                var batchHandler = new DropFirstBatchResponse(factory.Server.CreateHandler());
                using (var batchHttp = new HttpClient(batchHandler))
                {
                    var sender = new AgentBatchClient(batchHttp, TimeProvider.System);
                    await Assert.ThrowsAsync<HttpRequestException>(() => sender.SendNextAsync(vault, server, token));
                    Assert.AreEqual(batch.BatchId, (await queue.PeekAsync(token))!.BatchId);
                    await using (var db = await agents.CreateDbContextAsync(token))
                    {
                        Assert.AreEqual(1, await db.ReceivedBatches.CountAsync(token));
                        Assert.AreEqual(1, await db.RuntimeObservations.CountAsync(token));
                    }
                    Assert.AreEqual(BatchSendResult.Delivered, (await sender.SendNextAsync(vault, server, token)).Status);
                    Assert.IsNull(await queue.PeekAsync(token));
                    await using var dbAfter = await agents.CreateDbContextAsync(token);
                    Assert.AreEqual(1, await dbAfter.ReceivedBatches.CountAsync(token));
                    Assert.AreEqual(1, await dbAfter.RuntimeObservations.CountAsync(token));
                    var more = Enumerable.Range(0, 12).Select(_ => report with { MessageId = Guid.CreateVersion7(), OperatingSystem = new string('A', 200) }).ToArray();
                    var compressedBatch = new AgentBatch(1, Guid.CreateVersion7(), reportAt, more);
                    Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(compressedBatch.BatchId, reportAt, more.Length, AgentBatchCodec.Serialize(compressedBatch), token));
                    await Assert.ThrowsAsync<HttpRequestException>(() => sender.SendNextAsync(vault, server, token));
                    Assert.AreEqual(compressedBatch.BatchId, (await queue.PeekAsync(token))!.BatchId);
                    Assert.AreEqual(BatchSendResult.Delivered, (await sender.SendNextAsync(vault, server, token)).Status);
                    Assert.IsTrue(batchHandler.SawGzip);
                    Assert.AreEqual(2, await dbAfter.ReceivedBatches.CountAsync(token));
                    Assert.AreEqual(13, await dbAfter.RuntimeObservations.CountAsync(token));
                    var conflicting = AgentBatchCodec.Serialize(batch with { Items = [report with { RuntimeVersion = "different" }] });
                    Assert.AreEqual(EnqueueResult.Queued, await queue.EnqueueAsync(batch.BatchId, reportAt, 1, conflicting, token));
                    Assert.AreEqual(BatchSendResult.Rejected, (await sender.SendNextAsync(vault, server, token)).Status);
                    Assert.IsNull(await queue.PeekAsync(token));
                    var quarantined = await queue.GetStatusAsync(token);
                    Assert.AreEqual(1L, quarantined.TotalBatches, "A permanent rejection must retain the batch.");
                    Assert.AreEqual(1L, quarantined.QuarantinedBatches);
                }
                using (var heartbeatHttp = new HttpClient(new DropFirstQueueSessionResponse(factory.Server.CreateHandler())))
                {
                    await using var scope = factory.Services.CreateAsyncScope();
                    var configuration = scope.ServiceProvider.GetRequiredService<AgentConfiguration>();
                    var settingsMonitoring = scope.ServiceProvider.GetRequiredService<AgentSettingsMonitoring>();
                    var expectedSettings = (await configuration.UpdateAsync(device.Id, new AgentSettingsUpdate(new(60, null, 10), ""), Guid.CreateVersion7(), token))!;
                    using var settingsHttp = new HttpClient(factory.Server.CreateHandler());
                    var settingsClient = new AgentSettingsClient(settingsHttp, TimeProvider.System);
                    var synchronized = await settingsClient.SyncAsync(vault, server, token);
                    Assert.AreEqual(AgentSettingsSyncStatus.Saved, synchronized.Status);
                    Assert.AreEqual(expectedSettings.Effective, synchronized.Settings!.Snapshot);
                    var settingsReport = new AgentConfigurationReport(synchronized.Settings.Snapshot, synchronized.Settings.AppliedAt, null);
                    var observation = new AgentConfigurationObservation(new AgentSettingsOwner(recoveredId, server.AbsoluteUri, vault.StateDirectory), settingsReport);
                    var heartbeatClient = new AgentHeartbeatClient(heartbeatHttp);
                    Assert.AreEqual(HeartbeatResult.Accepted, await heartbeatClient.SendAsync(vault, server, observation, token));
                    var monitoring = scope.ServiceProvider.GetRequiredService<AgentMonitoring>();
                    Assert.IsNull((await monitoring.GetAsync(device.Id, token))!.Queue, "Lost session response still allows an empty heartbeat.");
                    Assert.AreEqual(HeartbeatResult.Accepted, await heartbeatClient.SendAsync(vault, server, observation, token));
                    var queueStatus = (await monitoring.GetAsync(device.Id, token))!.Queue;
                    Assert.AreEqual(clock.Now, queueStatus!.ReportedAt);
                    Assert.AreEqual(1L, queueStatus.Snapshot!.TotalBatches);
                    Assert.AreEqual(1L, queueStatus.Snapshot.QuarantinedBatches);
                    Assert.AreEqual(AgentSettingsApplyState.Applied, (await settingsMonitoring.GetAsync(device.Id, token))!.State);
                    var changedSettings = (await configuration.UpdateAsync(device.Id, new AgentSettingsUpdate(new(90, null, 10), expectedSettings.Version), Guid.CreateVersion7(), token))!;
                    Assert.AreEqual(AgentSettingsApplyState.Pending, (await settingsMonitoring.GetAsync(device.Id, token))!.State);
                    var updated = await settingsClient.SyncAsync(vault, server, token);
                    Assert.AreEqual(AgentSettingsSyncStatus.Saved, updated.Status);
                    Assert.AreEqual(changedSettings.Effective, updated.Settings!.Snapshot);
                    var nextReport = new AgentConfigurationReport(updated.Settings.Snapshot, updated.Settings.AppliedAt, null);
                    Assert.AreEqual(HeartbeatResult.Accepted, await heartbeatClient.SendAsync(vault, server, observation with { Report = nextReport }, token));
                    Assert.AreEqual(AgentSettingsApplyState.Applied, (await settingsMonitoring.GetAsync(device.Id, token))!.State);
                }
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
                var readerAccount = new Account { UserName = "reader" };
                Assert.IsTrue((await users.CreateAsync(readerAccount, "Strong-Password-123")).Succeeded);
                Assert.IsTrue((await users.AddToRoleAsync(readerAccount, BuiltInRoles.ReadOnly)).Succeeded);
                var adminAccount = await users.FindByNameAsync("admin");
                Assert.IsNotNull(adminAccount);
                var bytes = RandomNumberGenerator.GetBytes(32);
                var credential = new ApiCredential("test", SHA256.HashData(bytes), false, DateTimeOffset.UtcNow.AddDays(1), adminAccount.Id);
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
            var monitoringPaths = new[] { $"/api/v1/devices/{device.Id:D}/agent-status", $"/api/v1/devices/{device.Id:D}/agent-settings", $"/api/v1/devices/{device.Id:D}/agent-settings-status" };
            foreach (var path in monitoringPaths)
            {
                using var anonymousRead = await agent.GetAsync(path, token);
                Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousRead.StatusCode, path);
                using var apiRead = await api.GetAsync(path, token);
                Assert.AreEqual(HttpStatusCode.OK, apiRead.StatusCode, path);
            }
            using (var anonymousStatus = await agent.GetAsync($"/api/v1/devices/{device.Id:D}/agent-status", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousStatus.StatusCode);
            using (var apiStatus = await api.GetAsync($"/api/v1/devices/{device.Id:D}/agent-status", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, apiStatus.StatusCode);
                Assert.IsTrue(apiStatus.Headers.CacheControl?.NoStore);
                Assert.AreEqual(AgentConnectionState.NotRegistered, (await apiStatus.Content.ReadFromJsonAsync<AgentConnectionStatus>(cancellationToken: token))!.State);
            }
            using (var apiCannotImpersonate = await api.GetAsync("/api/v1/agent/identity", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, apiCannotImpersonate.StatusCode);
            using (var apiCannotHeartbeat = await api.PostAsync("/api/v1/agent/heartbeat", null, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, apiCannotHeartbeat.StatusCode);
            using (var apiCannotReadMachineSettings = await api.GetAsync("/api/v1/agent/settings", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, apiCannotReadMachineSettings.StatusCode);
            using (var apiCannotBatch = await api.PostAsync("/api/v1/agent/batches", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), token)) Assert.AreEqual(HttpStatusCode.Unauthorized, apiCannotBatch.StatusCode);
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
            foreach (var path in monitoringPaths)
            {
                using var adminRead = await admin.GetAsync(path, token);
                Assert.AreEqual(HttpStatusCode.OK, adminRead.StatusCode, path);
            }
            using var reader = factory.CreateClient(options);
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
                using var effectiveSettings = await reader.GetAsync($"/api/v1/devices/{device.Id:D}/agent-settings", token);
                Assert.AreEqual(HttpStatusCode.OK, effectiveSettings.StatusCode);
                Assert.AreEqual(AgentSettings.Default, (await effectiveSettings.Content.ReadFromJsonAsync<AgentSettingsSnapshot>(cancellationToken: token))!.Settings);
                using var settingsStatus = await reader.GetAsync($"/api/v1/devices/{device.Id:D}/agent-settings-status", token);
                Assert.AreEqual(HttpStatusCode.OK, settingsStatus.StatusCode);
                Assert.AreEqual(AgentSettingsApplyState.NotRegistered, (await settingsStatus.Content.ReadFromJsonAsync<AgentSettingsStatus>(cancellationToken: token))!.State);
                using var manageSettings = await reader.GetAsync("/api/v1/agent-management/settings/platform", token);
                Assert.AreEqual(HttpStatusCode.Forbidden, manageSettings.StatusCode);
                using var changeSettings = await reader.PutAsJsonAsync("/api/v1/agent-management/settings/platform", new AgentSettingsUpdate(new(60, null, null), ""), token);
                Assert.AreEqual(HttpStatusCode.Forbidden, changeSettings.StatusCode);
                using var details = await reader.GetAsync($"/devices/{device.Id:D}", token);
                var detailsDoc = await new HtmlParser().ParseDocumentAsync(await details.Content.ReadAsStringAsync(token), token);
                Assert.Contains("Agent 狀態", detailsDoc.Body!.TextContent);
                Assert.Contains("未註冊或已撤銷", detailsDoc.Body.TextContent);
                Assert.Contains("尚無佇列回報", detailsDoc.Body.TextContent);
                Assert.IsNull(detailsDoc.QuerySelector($"a[href='/devices/{device.Id:D}/agent']"));
                using var denied = await reader.PostAsync($"/api/v1/agent-management/devices/{device.Id:D}/enrollments", null, token);
                Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            }
            using (var forged = await admin.PostAsync($"/api/v1/agent-management/devices/{device.Id:D}/enrollments", null, token))
                Assert.AreEqual(HttpStatusCode.BadRequest, forged.StatusCode);
            using (var forgedSettings = await admin.PutAsJsonAsync("/api/v1/agent-management/settings/platform", new AgentSettingsUpdate(new(60, null, null), ""), token))
                Assert.AreEqual(HttpStatusCode.BadRequest, forgedSettings.StatusCode);
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
            AgentSettingsView configured;
            using (var initialSettings = await admin.GetAsync($"/api/v1/agent-management/settings/devices/{device.Id:D}", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, initialSettings.StatusCode);
                Assert.AreEqual("", (await initialSettings.Content.ReadFromJsonAsync<AgentSettingsView>(cancellationToken: token))!.Version);
            }
            using (var saveSettings = await admin.PutAsJsonAsync($"/api/v1/agent-management/settings/devices/{device.Id:D}", new AgentSettingsUpdate(new(60, null, null), ""), token))
            {
                Assert.AreEqual(HttpStatusCode.OK, saveSettings.StatusCode);
                configured = (await saveSettings.Content.ReadFromJsonAsync<AgentSettingsView>(cancellationToken: token))!;
                Assert.AreEqual(60, configured.Effective.Settings.HeartbeatSeconds);
            }
            using (var staleSettings = await admin.PutAsJsonAsync($"/api/v1/agent-management/settings/devices/{device.Id:D}", new AgentSettingsUpdate(new(90, null, null), ""), token))
                Assert.AreEqual(HttpStatusCode.Conflict, staleSettings.StatusCode);
            using (var invalidSettings = await admin.PutAsJsonAsync($"/api/v1/agent-management/settings/devices/{device.Id:D}", new AgentSettingsUpdate(new(1, null, null), configured.Version), token))
                Assert.AreEqual(HttpStatusCode.BadRequest, invalidSettings.StatusCode);
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
            foreach (var path in monitoringPaths)
            {
                using var machineRead = await agent.GetAsync(path, token);
                Assert.AreEqual(HttpStatusCode.Unauthorized, machineRead.StatusCode, path);
            }
            using (var valid = await agent.GetAsync("/api/v1/agent/identity", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, valid.StatusCode);
                Assert.AreEqual(device.Id, (await valid.Content.ReadFromJsonAsync<AgentIdentityResponse>(cancellationToken: token))!.DeviceId);
            }
            using (var settingsResponse = await agent.GetAsync("/api/v1/agent/settings", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, settingsResponse.StatusCode);
                Assert.AreEqual(configured.Effective, await settingsResponse.Content.ReadFromJsonAsync<AgentSettingsSnapshot>(cancellationToken: token));
                Assert.AreEqual($"\"{configured.Effective.Revision}\"", settingsResponse.Headers.ETag!.Tag);
                Assert.IsTrue(settingsResponse.Headers.CacheControl?.NoStore);
            }
            using (var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent/settings"))
            {
                conditional.Headers.IfNoneMatch.ParseAdd($"W/\"{configured.Effective.Revision}\"");
                using var unchanged = await agent.SendAsync(conditional, token);
                Assert.AreEqual(HttpStatusCode.NotModified, unchanged.StatusCode);
            }
            using (var heartbeat = await agent.PostAsync("/api/v1/agent/heartbeat", null, token)) Assert.AreEqual(HttpStatusCode.NoContent, heartbeat.StatusCode);
            var queueEpoch = Guid.CreateVersion7();
            using (var session = await agent.PostAsJsonAsync("/api/v1/agent/queue-session", new AgentQueueSessionRequest(null, queueEpoch), token))
                Assert.AreEqual(HttpStatusCode.NoContent, session.StatusCode);
            var settingsReport = new AgentConfigurationReport(configured.Effective, DateTimeOffset.UtcNow, null);
            var queueReport = new AgentHeartbeatReport(1, queueEpoch, 1, new AgentQueueSnapshot(1234, 5, 2, 8192, 10), settingsReport);
            using (var report = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", queueReport, token))
                Assert.AreEqual(HttpStatusCode.NoContent, report.StatusCode);
            using (var statusResponse = await api.GetAsync($"/api/v1/devices/{device.Id:D}/agent-settings-status", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, statusResponse.StatusCode);
                var status = await statusResponse.Content.ReadFromJsonAsync<AgentSettingsStatus>(cancellationToken: token);
                Assert.AreEqual(AgentSettingsApplyState.Applied, status!.State);
                Assert.AreEqual(configured.Effective, status.Applied);
            }
            using (var invalid = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", queueReport with { Queue = queueReport.Queue! with { QuarantinedBatches = 6 } }, token))
                Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            using (var wrongVersion = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", queueReport with { Version = 2 }, token))
                Assert.AreEqual(HttpStatusCode.BadRequest, wrongVersion.StatusCode);
            using (var duplicate = await agent.PostAsync("/api/v1/agent/heartbeat", JsonBytes("{\"version\":1,\"version\":1,\"queue\":null}"u8.ToArray()), token))
                Assert.AreEqual(HttpStatusCode.BadRequest, duplicate.StatusCode);
            using (var oversized = await agent.PostAsync("/api/v1/agent/heartbeat", JsonBytes(new byte[2049]), token))
                Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
            var now = DateTimeOffset.UtcNow;
            var observation = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, now, "test operating system", "X64", ".NET 10");
            var batch = new AgentBatch(1, Guid.CreateVersion7(), now, [observation]);
            var batchBytes = AgentBatchCodec.Serialize(batch);
            using (var batchResponse = await agent.PostAsync("/api/v1/agent/batches", JsonBytes(batchBytes), token)) Assert.AreEqual(HttpStatusCode.NoContent, batchResponse.StatusCode);
            using (var batchRetry = await agent.PostAsync("/api/v1/agent/batches", JsonBytes(batchBytes), token)) Assert.AreEqual(HttpStatusCode.NoContent, batchRetry.StatusCode);
            using (var compressedRetry = await agent.PostAsync("/api/v1/agent/batches", GzipBytes(batchBytes), token)) Assert.AreEqual(HttpStatusCode.NoContent, compressedRetry.StatusCode);
            using (var changedBatch = await agent.PostAsync("/api/v1/agent/batches", JsonBytes(AgentBatchCodec.Serialize(batch with { Items = [observation with { RuntimeVersion = "changed" }] })), token))
                Assert.AreEqual(HttpStatusCode.Conflict, changedBatch.StatusCode);
            using (var invalidBatch = await agent.PostAsync("/api/v1/agent/batches", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), token))
                Assert.AreEqual(HttpStatusCode.BadRequest, invalidBatch.StatusCode);
            using (var unsupported = JsonBytes(batchBytes))
            {
                unsupported.Headers.ContentEncoding.Add("br");
                using var rejected = await agent.PostAsync("/api/v1/agent/batches", unsupported, token);
                Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, rejected.StatusCode);
            }
            using (var invalidGzip = JsonBytes(batchBytes))
            {
                invalidGzip.Headers.ContentEncoding.Add("gzip");
                using var rejected = await agent.PostAsync("/api/v1/agent/batches", invalidGzip, token);
                Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
            }
            using (var complete = GzipBytes(batchBytes))
            {
                var encoded = await complete.ReadAsByteArrayAsync(token);
                using var truncated = JsonBytes(encoded[..^6]);
                truncated.Headers.ContentEncoding.Add("gzip");
                using var rejected = await agent.PostAsync("/api/v1/agent/batches", truncated, token);
                Assert.AreEqual(HttpStatusCode.BadRequest, rejected.StatusCode);
                encoded[^8] ^= 1;
                using var badChecksum = JsonBytes(encoded);
                badChecksum.Headers.ContentEncoding.Add("gzip");
                using var corrupt = await agent.PostAsync("/api/v1/agent/batches", badChecksum, token);
                Assert.AreEqual(HttpStatusCode.BadRequest, corrupt.StatusCode);
            }
            using (var compound = GzipBytes(batchBytes))
            {
                compound.Headers.ContentEncoding.Add("br");
                using var rejected = await agent.PostAsync("/api/v1/agent/batches", compound, token);
                Assert.AreEqual(HttpStatusCode.UnsupportedMediaType, rejected.StatusCode);
            }
            using (var oversizedGzip = await agent.PostAsync("/api/v1/agent/batches", GzipBytes(new byte[AgentBatchCodec.MaximumBytes + 1]), token))
                Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, oversizedGzip.StatusCode);
            using (var machineStatus = await agent.GetAsync($"/api/v1/devices/{device.Id:D}/agent-status", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, machineStatus.StatusCode);
            using (var connected = await api.GetAsync($"/api/v1/devices/{device.Id:D}/agent-status", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, connected.StatusCode);
                var status = await connected.Content.ReadFromJsonAsync<AgentConnectionStatus>(cancellationToken: token);
                Assert.AreEqual(AgentConnectionState.Online, status!.State);
                Assert.AreEqual(queueReport.Queue, status.Queue!.Snapshot);
                Assert.IsNotNull(status.LastHeartbeatAt);
            }
            using (var deliveryPage = await admin.GetAsync($"/devices/{device.Id:D}", token))
            {
                var page = await new HtmlParser().ParseDocumentAsync(await deliveryPage.Content.ReadAsStringAsync(token), token);
                var fields = page.QuerySelectorAll("dl div").Where(x => x.QuerySelector("dt") is not null)
                    .ToDictionary(x => x.QuerySelector("dt")!.TextContent, x => x.QuerySelector("dd")?.TextContent.Trim() ?? "");
                Assert.AreEqual("3 批", fields["等待傳送"]);
                Assert.AreEqual("2 批", fields["已隔離"]);
                Assert.AreEqual("5 / 10 批", fields["批次容量"]);
                Assert.Contains("可能不是目前狀態", page.Body!.TextContent);
            }
            using (var deliveryPage = await reader.GetAsync($"/devices/{device.Id:D}", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, deliveryPage.StatusCode);
                var page = await new HtmlParser().ParseDocumentAsync(await deliveryPage.Content.ReadAsStringAsync(token), token);
                Assert.Contains("等待傳送", page.Body!.TextContent);
                Assert.Contains("3 批", page.Body.TextContent);
            }
            using (var full = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", queueReport with { Sequence = 2, Queue = queueReport.Queue! with { TotalBatches = 10 } }, token))
                Assert.AreEqual(HttpStatusCode.NoContent, full.StatusCode);
            using (var deliveryPage = await reader.GetAsync($"/devices/{device.Id:D}", token))
            {
                var page = await new HtmlParser().ParseDocumentAsync(await deliveryPage.Content.ReadAsStringAsync(token), token);
                Assert.Contains("佇列已達容量", page.Body!.TextContent);
            }
            using (var unreadable = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", queueReport with { Sequence = 3, Queue = null }, token))
                Assert.AreEqual(HttpStatusCode.NoContent, unreadable.StatusCode);
            using (var deliveryPage = await admin.GetAsync($"/devices/{device.Id:D}", token))
            {
                var page = await new HtmlParser().ParseDocumentAsync(await deliveryPage.Content.ReadAsStringAsync(token), token);
                Assert.Contains("Agent 無法讀取佇列，資料狀態未知", page.Body!.TextContent);
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
            using (var revokedSettings = await agent.GetAsync("/api/v1/agent/settings", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, revokedSettings.StatusCode);
            using (var revokedBatch = await agent.PostAsync("/api/v1/agent/batches", JsonBytes(batchBytes), token)) Assert.AreEqual(HttpStatusCode.Unauthorized, revokedBatch.StatusCode);
        }, TestContext.CancellationToken);

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ByteArrayContent GzipBytes(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(bytes);
        var content = JsonBytes(output.ToArray());
        content.Headers.ContentEncoding.Add("gzip");
        return content;
    }

    private static ByteArrayContent JsonBytes(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private sealed class TestDeviceGate
    {
        public volatile bool Enabled;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class BlockingDeviceSource(IDeviceAgentSource inner, TestDeviceGate gate) : IDeviceAgentSource
    {
        public async Task<DeviceAgentState?> GetAsync(Guid deviceId, CancellationToken token)
        {
            if (gate.Enabled)
            {
                gate.Entered.TrySetResult();
                await gate.Release.Task.WaitAsync(token);
            }
            return await inner.GetAsync(deviceId, token);
        }

        public Task<bool> IsCurrentAsync(Guid deviceId, long generation, DbConnection connection, DbTransaction transaction, CancellationToken token) =>
            inner.IsCurrentAsync(deviceId, generation, connection, transaction, token);
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

    private sealed class DropFirstBatchResponse(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private bool dropped;
        private bool droppedGzip;
        public bool SawGzip { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri?.AbsolutePath == "/api/v1/agent/batches" && request.Content?.Headers.ContentEncoding.Contains("gzip") == true)
                SawGzip = true;
            var response = await base.SendAsync(request, token);
            if (!dropped && request.RequestUri?.AbsolutePath == "/api/v1/agent/batches")
            {
                dropped = true;
                response.Dispose();
                throw new HttpRequestException("Simulated connection loss after the batch commit.");
            }
            if (!droppedGzip && request.RequestUri?.AbsolutePath == "/api/v1/agent/batches" &&
                request.Content?.Headers.ContentEncoding.Contains("gzip") == true)
            {
                droppedGzip = true;
                response.Dispose();
                throw new HttpRequestException("Simulated connection loss after the compressed batch commit.");
            }
            return response;
        }
    }

    private sealed class DropFirstQueueSessionResponse(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private bool dropped;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (!dropped && request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/v1/agent/queue-session" && response.IsSuccessStatusCode)
            {
                dropped = true;
                response.Dispose();
                throw new HttpRequestException("Simulated connection loss after the queue session committed.");
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
