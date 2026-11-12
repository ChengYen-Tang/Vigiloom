extern alias AgentHarness;
using System.Diagnostics;
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
using Microsoft.Extensions.Logging;
using Vigiloom.Agent.Core.Identity;
using Vigiloom.Agent.Core.Configuration;
using Vigiloom.Agent.Core.Inventory;
using Vigiloom.Agent.Core.Transport;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Contracts.Devices;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Contracts.Resources;
using Vigiloom.Modules.Resources.Application;
using System.Data.Common;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Api;
using Vigiloom.Web.Authentication;
using Vigiloom.Infrastructure.LogAdmission;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AgentIdentityHttpTests
{
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public Task Inventory_RealAgentCycleQueuesSendsAndConfirmsThroughHttp() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            var parent = Path.Combine(Path.GetTempPath(), "VigiloomInventoryEndToEndTests");
            var directory = Path.Combine(parent, Path.GetRandomFileName());
            try
            {
                using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment("Development");
                    builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:Vigiloom"] = database.ConnectionString
                    }));
                });
                var resources = factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
                await using (var db = await resources.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
                var agents = factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>();
                await using (var db = await agents.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
                var security = factory.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>();
                await using (var db = await security.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
                var device = new Device("real inventory cycle", "127.0.0.1", true, DateTimeOffset.UtcNow);
                await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
                Guid agentId;
                var secret = RandomNumberGenerator.GetBytes(32);
                await using (var scope = factory.Services.CreateAsyncScope())
                {
                    var identity = scope.ServiceProvider.GetRequiredService<AgentIdentity>();
                    var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
                    var registered = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
                    agentId = registered.AgentId!.Value;
                }
                var server = new Uri("https://localhost/");
                var vault = new IdentityVault(directory);
                await using (var session = vault.Acquire())
                    await session.WriteAsync(new IdentityState { AgentId = agentId, Server = server.AbsoluteUri, MachineSecret = WebEncoders.Base64UrlEncode(secret) }, token);
                using var http = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = server });
                var cycle = new InventoryCycle(new InventoryStatusClient(http, TimeProvider.System), TimeProvider.System);
                InventoryReadResult<InventoryEntry>? observed = null;
                Task<InventoryReadResult<InventoryEntry>> Collect(InventorySource _, CancellationToken cancellation) =>
                    Task.FromResult(observed = HostHardwareReader.ReadNetwork(cancellation));
                var queued = await cycle.RunAsync(vault, server, InventorySource.HostNetwork, Collect, token);
                Assert.AreEqual(InventoryCycleKind.Queued, queued.Kind);
                var sender = new AgentBatchClient(http, TimeProvider.System);
                var queue = new DurableBatchQueue(vault, agentId);
                var sent = 0;
                while (await queue.PeekAsync(token) is not null)
                {
                    Assert.IsLessThan(InventoryChunkCodec.MaximumChunks + 1, ++sent);
                    Assert.AreEqual(BatchSendResult.Delivered, (await sender.SendNextAsync(vault, server, token)).Status);
                }
                var completed = await cycle.RunAsync(vault, server, InventorySource.HostNetwork, Collect, token);
                Assert.AreEqual(InventoryCycleKind.Complete, completed.Kind);
                Assert.AreEqual(0L, (await queue.GetStatusAsync(token)).TotalBatches);
                await using var verify = await security.CreateDbContextAsync(token);
                var state = await verify.InventorySources.AsNoTracking().SingleAsync(x => x.AgentId == agentId && x.Source == InventorySource.HostNetwork, token);
                Assert.IsNotNull(observed);
                Assert.AreEqual((InventoryOutcome)observed.Status, state.LastOutcome);
                Assert.AreEqual(observed.Status == InventoryReadStatus.Complete ? 1L : null, state.CurrentGeneration);

                InventoryReadResult<InventoryEntry>? processObservation = null;
                Task<InventoryReadResult<InventoryEntry>> CollectProcesses(InventorySource _, CancellationToken cancellation)
                {
                    var actual = Vigiloom.Agent.Core.Processes.ProcessReader.Read(cancellation);
                    // Force the partial path to model a host with inaccessible processes while
                    // retaining actual OS observations from two independent sampling passes.
                    processObservation = new(InventoryReadStatus.Partial, actual.Items.Select(x => new InventoryEntry(x.StableKey, x.Name,
                        NumericId: (uint)x.ProcessId, ProcessStartedAt: x.StartedAt, ParentProcessId: x.ParentProcessId, RunAs: x.RunAs)).ToArray());
                    return Task.FromResult(processObservation);
                }
                Assert.AreEqual(InventoryCycleKind.Queued, (await cycle.RunAsync(vault, server, InventorySource.HostProcesses, CollectProcesses, token)).Kind);
                var processSent = 0;
                while (await queue.PeekAsync(token) is not null)
                {
                    Assert.IsLessThan(InventoryChunkCodec.MaximumChunks + 1, ++processSent);
                    Assert.AreEqual(BatchSendResult.Delivered, (await sender.SendNextAsync(vault, server, token)).Status);
                }
                Assert.AreEqual(InventoryCycleKind.Complete, (await cycle.RunAsync(vault, server, InventorySource.HostProcesses, CollectProcesses, token)).Kind);
                Assert.IsNotNull(processObservation);
                Assert.IsTrue(processObservation.Items.Any(x => x.NumericId == (uint)Environment.ProcessId));
                var processState = await verify.InventorySources.AsNoTracking().SingleAsync(x => x.AgentId == agentId && x.Source == InventorySource.HostProcesses, token);
                Assert.AreEqual((InventoryOutcome)processObservation.Status, processState.LastOutcome);

                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add(typeof(AgentHarness::AgentHostHarnessMarker).Assembly.Location);
                start.ArgumentList.Add("--hold-for-inventory");
                using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the test process.");
                try
                {
                    Assert.AreEqual("READY", await child.StandardOutput.ReadLineAsync(token));
                    Assert.AreEqual(InventoryCycleKind.Queued, (await cycle.RunAsync(vault, server, InventorySource.HostProcesses, CollectProcesses, token)).Kind);
                    while (await queue.PeekAsync(token) is not null)
                        Assert.AreEqual(BatchSendResult.Delivered, (await sender.SendNextAsync(vault, server, token)).Status);
                    Assert.AreEqual(InventoryCycleKind.Complete, (await cycle.RunAsync(vault, server, InventorySource.HostProcesses, CollectProcesses, token)).Kind);
                    var queryNow = DateTimeOffset.UtcNow;
                    var changes = await new InventoryChangeQuery(security).GetAsync(device.Id, queryNow.AddMinutes(-1), queryNow.AddMinutes(1), InventorySource.HostProcesses, null, token);
                    Assert.IsTrue(changes.Items.Any(x => x.Kind == InventoryChangeType.NewlyObserved && x.After?.NumericId == (uint)child.Id));
                    Assert.IsFalse(changes.Items.Any(x => x.Kind == InventoryChangeType.Removed));
                }
                finally
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await child.WaitForExitAsync(cleanup.Token);
                    }
                }
            }
            finally
            {
                var prefix = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
                if (!Path.GetFullPath(directory).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidOperationException("Test cleanup escaped its temporary root.");
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        }, TestContext.CancellationToken);

    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public Task Inventory_HttpCommitsAndReturnsOnlyAuthenticatedStatus() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Vigiloom"] = database.ConnectionString
                }));
            });
            var resources = factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
            await using (var db = await resources.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var agents = factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>();
            await using (var db = await agents.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var security = factory.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>();
            await using (var db = await security.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var now = DateTimeOffset.UtcNow;
            var device = new Device("inventory host", "127.0.0.1", true, now);
            await using (var db = await resources.CreateDbContextAsync(token)) { db.Devices.Add(device); await db.SaveChangesAsync(token); }
            string credential;
            Guid registeredAgentId;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var identity = scope.ServiceProvider.GetRequiredService<AgentIdentity>();
                var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
                var secret = RandomNumberGenerator.GetBytes(32);
                var registered = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
                registeredAgentId = registered.AgentId ?? throw new InvalidOperationException("Agent enrollment did not return an identity.");
                credential = $"vla_{registered.AgentId:D}.{WebEncoders.Base64UrlEncode(secret)}";
            }
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
            using (var anonymous = await client.GetAsync("/api/v1/agent/inventory/status", token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            using (var anonymous = await client.GetAsync("/api/v1/agent/inventory-settings", token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            using (var policy = await client.GetAsync("/api/v1/agent/inventory-settings", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, policy.StatusCode);
                Assert.IsTrue(policy.Headers.CacheControl!.NoStore);
                var settings = await policy.Content.ReadFromJsonAsync<InventorySettingsSnapshot>(cancellationToken: token);
                Assert.IsNotNull(settings);
                Assert.AreEqual(21600, settings.Settings.ScanIntervalSeconds);
                using var conditional = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent/inventory-settings");
                conditional.Headers.IfNoneMatch.ParseAdd($"\"{settings.Revision}\"");
                using var unchanged = await client.SendAsync(conditional, token);
                Assert.AreEqual(HttpStatusCode.NotModified, unchanged.StatusCode);
            }
            var epoch = Guid.CreateVersion7();
            using (var started = await client.PostAsJsonAsync("/api/v1/agent/queue-session", new AgentQueueSessionRequest(null, epoch), token))
                Assert.AreEqual(HttpStatusCode.NoContent, started.StatusCode);
            var appliedInventory = InventorySettingsSnapshot.Create(InventorySettings.Default);
            var heartbeat = new AgentHeartbeatReport(1, epoch, 1, null, InventoryConfiguration: new(appliedInventory, now, null));
            using (var reported = await client.PostAsJsonAsync("/api/v1/agent/heartbeat", heartbeat, token))
                Assert.AreEqual(HttpStatusCode.NoContent, reported.StatusCode);
            var measured = InventoryDigest.MeasureChunk([]);
            var chunk = new InventoryChunk(4, Guid.CreateVersion7(), now, Guid.CreateVersion7(), 1,
                InventorySource.LinuxDpkg, now, InventoryOutcome.Complete, 0, 1, 0, measured.Bytes,
                InventoryDigest.Combine([measured.Digest]), []);
            var payload = InventoryChunkCodec.Serialize(chunk);
            using (var accepted = await client.PostAsync("/api/v1/agent/batches", JsonBytes(payload), token))
            {
                Assert.AreEqual(HttpStatusCode.NoContent, accepted.StatusCode);
                Assert.IsTrue(accepted.Headers.TryGetValues(AgentMetricBatchCodec.SupportedSchemaHeader, out var supported));
                Assert.Contains("4", string.Join(",", supported));
            }
            using (var duplicate = await client.PostAsync("/api/v1/agent/batches", JsonBytes(payload), token))
                Assert.AreEqual(HttpStatusCode.NoContent, duplicate.StatusCode);
            using var statusResponse = await client.GetAsync("/api/v1/agent/inventory/status", token);
            Assert.AreEqual(HttpStatusCode.OK, statusResponse.StatusCode);
            Assert.IsNotNull(statusResponse.Headers.CacheControl);
            Assert.IsTrue(statusResponse.Headers.CacheControl.NoStore);
            var status = await statusResponse.Content.ReadFromJsonAsync<InventorySourceStatus[]>(cancellationToken: token);
            Assert.IsNotNull(status);
            Assert.HasCount(1, status);
            Assert.AreEqual(1L, status[0].HighWaterGeneration);
            Assert.AreEqual(chunk.SnapshotId, status[0].LastSnapshotId);
            Assert.AreEqual(InventoryCompletion.Complete, status[0].LastStatus);
            var items = new[] { new InventoryEntry("package", "Example package", "1.0", "x64") };
            var nextMeasure = InventoryDigest.MeasureChunk(items);
            var next = chunk with { BatchId = Guid.CreateVersion7(), SnapshotId = Guid.CreateVersion7(), Generation = 2,
                TotalItems = 1, TotalBytes = nextMeasure.Bytes, Digest = InventoryDigest.Combine([nextMeasure.Digest]), Items = items };
            using (var accepted = await client.PostAsync("/api/v1/agent/batches", JsonBytes(InventoryChunkCodec.Serialize(next)), token))
                Assert.AreEqual(HttpStatusCode.NoContent, accepted.StatusCode);
            var inventoryUrl = $"/api/v1/devices/{device.Id:D}/inventory";
            using (var denied = await client.GetAsync(inventoryUrl, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            await PingTestSite.InitializeAsync(factory, token);
            using var reader = PingTestSite.Client(factory);
            await PingTestSite.LoginAsync(reader, "reader", token);
            using (var policyStatus = await reader.GetAsync($"/api/v1/devices/{device.Id:D}/inventory-settings-status", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, policyStatus.StatusCode);
                var reported = await policyStatus.Content.ReadFromJsonAsync<InventorySettingsStatus>(cancellationToken: token);
                Assert.IsNotNull(reported);
                Assert.AreEqual(AgentSettingsApplyState.Applied, reported.State);
                Assert.AreEqual(appliedInventory.Revision, reported.Applied!.Revision);
            }
            using var viewResponse = await reader.GetAsync(inventoryUrl, token);
            Assert.AreEqual(HttpStatusCode.OK, viewResponse.StatusCode);
            var view = await viewResponse.Content.ReadFromJsonAsync<DeviceInventoryView>(cancellationToken: token);
            Assert.IsNotNull(view);
            Assert.HasCount(1, view.Sources);
            Assert.AreEqual(next.SnapshotId, view.Sources[0].CurrentSnapshotId);
            Assert.AreEqual(1, view.Sources[0].CurrentItemCount);
            using var chunkResponse = await reader.GetAsync($"{inventoryUrl}/{(int)next.Source}/snapshots/{next.SnapshotId:D}/chunks/0", token);
            Assert.AreEqual(HttpStatusCode.OK, chunkResponse.StatusCode);
            var readChunk = await chunkResponse.Content.ReadFromJsonAsync<InventorySnapshotChunkView>(cancellationToken: token);
            Assert.IsNotNull(readChunk);
            Assert.AreEqual("Example package", readChunk.Items[0].Name);
            using var stale = await reader.GetAsync($"{inventoryUrl}/{(int)chunk.Source}/snapshots/{chunk.SnapshotId:D}/chunks/0", token);
            Assert.AreEqual(HttpStatusCode.NotFound, stale.StatusCode);
            var changesUrl = $"{inventoryUrl}/changes?from={Uri.EscapeDataString(now.AddHours(-1).ToString("O"))}&until={Uri.EscapeDataString(now.AddHours(1).ToString("O"))}";
            using (var denied = await client.GetAsync(changesUrl, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            using var changesResponse = await reader.GetAsync(changesUrl, token);
            Assert.AreEqual(HttpStatusCode.OK, changesResponse.StatusCode);
            var changes = await changesResponse.Content.ReadFromJsonAsync<InventoryChangePage>(cancellationToken: token);
            Assert.IsNotNull(changes);
            Assert.HasCount(1, changes.Items);
            Assert.AreEqual(InventoryChangeType.Added, changes.Items[0].Kind);
            Assert.AreEqual("Example package", changes.Items[0].After!.Name);
            var processOne = new[] { new InventoryEntry("linux:boot:42:1", "worker", NumericId: 42, ProcessStartedAt: now) };
            var processTwo = new[] { new InventoryEntry("linux:boot:43:2", "server", NumericId: 43, ProcessStartedAt: now) };
            var firstMeasure = InventoryDigest.MeasureChunk(processOne);
            var secondMeasure = InventoryDigest.MeasureChunk(processTwo);
            var processChunk = new InventoryChunk(5, Guid.CreateVersion7(), now, Guid.CreateVersion7(), 1,
                InventorySource.HostProcesses, now, InventoryOutcome.Complete, 0, 2, 2,
                firstMeasure.Bytes + secondMeasure.Bytes, InventoryDigest.Combine([firstMeasure.Digest, secondMeasure.Digest]), processOne);
            using (var staged = await client.PostAsync("/api/v1/agent/batches", JsonBytes(InventoryChunkCodec.Serialize(processChunk)), token))
                Assert.AreEqual(HttpStatusCode.NoContent, staged.StatusCode);
            using (var legacyBusy = await client.GetAsync("/api/v1/agent/inventory/status", token))
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, legacyBusy.StatusCode);
            using (var extended = await client.GetAsync("/api/v1/agent/inventory/status?contract=5", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, extended.StatusCode);
                Assert.AreEqual("5", extended.Headers.GetValues("Vigiloom-Inventory-Contract").Single());
                var extendedSources = await extended.Content.ReadFromJsonAsync<InventorySourceStatus[]>(cancellationToken: token);
                Assert.IsNotNull(extendedSources);
                Assert.IsTrue(extendedSources.Any(x => x.Source == InventorySource.HostProcesses && x.ActiveGeneration == 1));
            }
            var lastProcessChunk = processChunk with { BatchId = Guid.CreateVersion7(), ChunkIndex = 1, Items = processTwo };
            using (var acceptedProcess = await client.PostAsync("/api/v1/agent/batches", JsonBytes(InventoryChunkCodec.Serialize(lastProcessChunk)), token))
                Assert.AreEqual(HttpStatusCode.NoContent, acceptedProcess.StatusCode);
            var processUrl = $"{inventoryUrl}/{(int)InventorySource.HostProcesses}/snapshots/{processChunk.SnapshotId:D}/chunks/0";
            using (var deniedProcess = await client.GetAsync(processUrl, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, deniedProcess.StatusCode);
            using (var allowedProcess = await reader.GetAsync(processUrl, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, allowedProcess.StatusCode);
                var processData = await allowedProcess.Content.ReadFromJsonAsync<InventorySnapshotChunkView>(cancellationToken: token);
                Assert.IsNotNull(processData);
                Assert.AreEqual(42U, processData.Items.Single().NumericId);
            }
            using (var legacy = await client.GetAsync("/api/v1/agent/inventory/status", token))
            {
                var legacySources = await legacy.Content.ReadFromJsonAsync<InventorySourceStatus[]>(cancellationToken: token);
                Assert.IsNotNull(legacySources);
                Assert.HasCount(1, legacySources);
                Assert.AreEqual(InventorySource.LinuxDpkg, legacySources[0].Source);
            }
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var policy = scope.ServiceProvider.GetRequiredService<InventoryConfiguration>();
                var platform = await policy.GetAsync(null, token);
                Assert.IsNotNull(platform);
                Assert.IsNotNull(await policy.UpdateAsync(null, new(new(null, true), platform.Version), Guid.CreateVersion7(), token));
            }
            using (var legacyPolicy = await client.GetAsync("/api/v1/agent/inventory-settings", token))
            {
                var oldSnapshot = await legacyPolicy.Content.ReadFromJsonAsync<InventorySettingsSnapshot>(cancellationToken: token);
                Assert.IsNotNull(oldSnapshot);
                Assert.AreEqual(InventorySettingsSnapshot.CurrentSchema, oldSnapshot.SchemaVersion);
                Assert.IsFalse(oldSnapshot.Settings.CollectCommandLine);
                Assert.IsTrue(oldSnapshot.IsValid());
            }
            using (var newPolicy = await client.GetAsync("/api/v1/agent/inventory-settings?contract=2", token))
            {
                var newSnapshot = await newPolicy.Content.ReadFromJsonAsync<InventorySettingsSnapshot>(cancellationToken: token);
                Assert.IsNotNull(newSnapshot);
                Assert.AreEqual(InventorySettingsSnapshot.ProcessSchema, newSnapshot.SchemaVersion);
                Assert.IsTrue(newSnapshot.Settings.CollectCommandLine);
                Assert.IsTrue(newSnapshot.IsValid());
            }
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var identity = scope.ServiceProvider.GetRequiredService<AgentIdentity>();
                Assert.AreEqual(AgentIdentityStatus.Success, await identity.RevokeAsync(registeredAgentId, Guid.CreateVersion7(), token));
            }
            using var revoked = new HttpRequestMessage(HttpMethod.Get, "/api/v1/agent/inventory-settings");
            revoked.Headers.IfNoneMatch.ParseAdd($"\"{appliedInventory.Revision}\"");
            using var revokedResponse = await client.SendAsync(revoked, token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, revokedResponse.StatusCode);
        }, TestContext.CancellationToken);

    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public Task HostLogBatch_HttpPreservesReceiptAndReportsQuota() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Vigiloom"] = database.ConnectionString
                }));
            });
            var resources = factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
            await using (var db = await resources.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var agents = factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>();
            await using (var db = await agents.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var quota = factory.Services.GetRequiredService<IDbContextFactory<LogAdmissionDbContext>>();
            await using (var db = await quota.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var administration = new PolicyAdministration(quota);
            Assert.IsNotNull(await administration.UpdateAsync(new(1, 1, 1, 1), "", Guid.CreateVersion7(),
                (_, _, _, _) => Task.CompletedTask, token));
            var now = DateTimeOffset.UtcNow;
            var device = new Device("log host", "127.0.0.1", true, now);
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
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            var item = new AgentLogRecord(Guid.CreateVersion7(), now, "event", null, null, null, null, null);
            var batch = new AgentLogBatch(3, Guid.CreateVersion7(), now, Guid.CreateVersion7(), AgentLogSourceKind.Journal, "system", 1, [item]);
            var bytes = AgentLogBatchCodec.Serialize(batch);
            using (var accepted = await client.PostAsync("/api/v1/agent/batches", JsonBytes(bytes), token))
            {
                Assert.AreEqual(HttpStatusCode.NoContent, accepted.StatusCode);
                Assert.IsTrue(accepted.Headers.TryGetValues(AgentMetricBatchCodec.SupportedSchemaHeader, out var supported));
                Assert.Contains("3", string.Join(",", supported));
            }
            await using (var db = await agents.CreateDbContextAsync(token))
                await db.Database.ExecuteSqlRawAsync("UPDATE [logquota].[Buckets] SET [Microtokens] = 0, [LastRefill] = TODATETIMEOFFSET(DATEADD(hour, 1, SYSUTCDATETIME()), '+00:00') WHERE [Kind] = 0", token);
            using (var duplicate = await client.PostAsync("/api/v1/agent/batches", JsonBytes(bytes), token)) Assert.AreEqual(HttpStatusCode.NoContent, duplicate.StatusCode);
            var next = batch with { BatchId = Guid.CreateVersion7(), Items = [item with { MessageId = Guid.CreateVersion7() }] };
            using (var limited = await client.PostAsync("/api/v1/agent/batches", JsonBytes(AgentLogBatchCodec.Serialize(next)), token))
            {
                Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
                Assert.IsNotNull(limited.Headers.RetryAfter?.Delta);
                Assert.IsGreaterThan(TimeSpan.Zero, limited.Headers.RetryAfter.Delta.Value);
            }
            var tooLarge = next with { BatchId = Guid.CreateVersion7(), Items = [item with { MessageId = Guid.CreateVersion7() }, item with { MessageId = Guid.CreateVersion7() }] };
            using (var rejected = await client.PostAsync("/api/v1/agent/batches", JsonBytes(AgentLogBatchCodec.Serialize(tooLarge)), token))
                Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
            await using var verify = await agents.CreateDbContextAsync(token);
            Assert.AreEqual(1, await verify.HostLogs.CountAsync(token));
            Assert.AreEqual(1, await verify.ReceivedBatches.CountAsync(token));
            await PingTestSite.InitializeAsync(factory, token);
            var historyUrl = $"/api/v1/devices/{device.Id:D}/host-logs?from={Uri.EscapeDataString(now.AddHours(-1).ToString("O"))}&until={Uri.EscapeDataString(now.AddHours(1).ToString("O"))}";
            using (var machineDenied = await client.GetAsync(historyUrl, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, machineDenied.StatusCode);
            using var reader = PingTestSite.Client(factory);
            await PingTestSite.LoginAsync(reader, "reader", token);
            using var response = await reader.GetAsync(historyUrl, token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.CacheControl?.NoStore);
            var history = await response.Content.ReadFromJsonAsync<HostLogPage>(cancellationToken: token);
            Assert.IsNotNull(history); Assert.HasCount(1, history.Items);
            Assert.AreEqual("event", history.Items[0].Content);
        }, TestContext.CancellationToken);

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
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
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
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
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
            using (var denied = await first.GetAsync("/api/v1/operations/platform-status", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            foreach (var source in new[] { "accounts", "api-credentials", "devices", "agent-identity", "agent-settings", "scheduling" })
                using (var denied = await first.GetAsync($"/api/v1/operations/audit-feed/{source}?from=2026-11-01T00%3A00%3A00Z&to=2026-11-02T00%3A00%3A00Z", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
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
                    var restartedVault = new IdentityVault(root);
                    var restartedQueue = new DurableBatchQueue(restartedVault, recoveredId);
                    var pending = await restartedQueue.PeekAsync(token);
                    Assert.AreEqual(batch.BatchId, pending!.BatchId);
                    CollectionAssert.AreEqual(bytes, pending.Payload, "Reopening the queue must retain the exact original wire bytes.");
                    var decoded = AgentBatchCodec.TryRead(pending.Payload, out var pendingBatch);
                    Assert.IsTrue(decoded);
                    Assert.AreEqual(report.MessageId, pendingBatch!.Items[0].MessageId);
                    await using (var db = await agents.CreateDbContextAsync(token))
                    {
                        Assert.AreEqual(1, await db.ReceivedBatches.CountAsync(token));
                        Assert.AreEqual(1, await db.RuntimeObservations.CountAsync(token));
                    }
                    var restartedSender = new AgentBatchClient(batchHttp, TimeProvider.System);
                    Assert.AreEqual(BatchSendResult.Delivered, (await restartedSender.SendNextAsync(restartedVault, server, token)).Status);
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
                    var ordered = await dbAfter.RuntimeObservations.AsNoTracking().Where(x => x.AgentId == recoveredId && x.BatchId == compressedBatch.BatchId)
                        .OrderBy(x => x.Ordinal).Select(x => new { x.Ordinal, x.MessageId }).ToArrayAsync(token);
                    CollectionAssert.AreEqual(more.Select((x, ordinal) => (ordinal, x.MessageId)).ToArray(),
                        ordered.Select(x => (x.Ordinal, x.MessageId)).ToArray(), "Message identity and ordinal survive retry and commit.");
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
                    var capacityConfiguration = scope.ServiceProvider.GetRequiredService<AgentQueueCapacityConfiguration>();
                    var capacityMonitoring = scope.ServiceProvider.GetRequiredService<AgentQueueCapacityMonitoring>();
                    var expectedCapacity = (await capacityConfiguration.UpdateAsync(device.Id, new AgentQueueCapacityUpdate(new(1024L * 1024, 100), ""), Guid.CreateVersion7(), token))!;
                    var capacityClient = new AgentQueueCapacityClient(settingsHttp, TimeProvider.System);
                    var capacitySync = await capacityClient.SyncAsync(vault, server, token);
                    Assert.AreEqual(AgentSettingsSyncStatus.Saved, capacitySync.Status);
                    Assert.AreEqual(expectedCapacity.Effective, capacitySync.Settings!.Snapshot);
                    Assert.AreEqual(AgentSettingsSyncStatus.Unchanged, (await capacityClient.SyncAsync(vault, server, token)).Status);
                    var capacityObservation = new AgentQueueCapacityObservation(observation.Owner, null);
                    var heartbeatClient = new AgentHeartbeatClient(heartbeatHttp);
                    Assert.AreEqual(HeartbeatResult.Accepted, await heartbeatClient.SendAsync(vault, server, observation, capacityObservation, token));
                    var monitoring = scope.ServiceProvider.GetRequiredService<AgentMonitoring>();
                    Assert.IsNull((await monitoring.GetAsync(device.Id, token))!.Queue, "Lost session response still allows an empty heartbeat.");
                    Assert.AreEqual(HeartbeatResult.Accepted, await heartbeatClient.SendAsync(vault, server, observation, capacityObservation, token));
                    var queueStatus = (await monitoring.GetAsync(device.Id, token))!.Queue;
                    Assert.AreEqual(clock.Now, queueStatus!.ReportedAt);
                    Assert.AreEqual(1L, queueStatus.Snapshot!.TotalBatches);
                    Assert.AreEqual(1L, queueStatus.Snapshot.QuarantinedBatches);
                    Assert.AreEqual(new AgentQuarantineBreakdown(0, 0, 1, 0, 0, 0, 0), queueStatus.Quarantine,
                        "A real rejected batch must be reported through the Agent heartbeat.");
                    Assert.AreEqual(AgentSettingsApplyState.Applied, (await settingsMonitoring.GetAsync(device.Id, token))!.State);
                    var capacityStatus = await capacityMonitoring.GetAsync(device.Id, token);
                    Assert.AreEqual(AgentSettingsApplyState.Applied, capacityStatus!.State);
                    Assert.AreEqual(expectedCapacity.Effective, capacityStatus.Applied);
                    Assert.AreEqual(1024L * 1024, queueStatus.Snapshot.CapacityBytes);
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
            var clock = new TestClock();
            using var captured = new CapturedLogs();
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Vigiloom"] = database.ConnectionString,
                    ["Vigiloom:SetupToken"] = setupSecret
                }));
                builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(clock));
                builder.ConfigureLogging(logging => logging.AddProvider(captured));
            });
            var identities = factory.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
            await using (var db = await identities.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var resources = factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>();
            await using (var db = await resources.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            var rootMetadata = System.Text.Encoding.UTF8.GetBytes("""{"signed":{"_type":"root","version":1},"signatures":[{}]}""");
            await using (var scope = factory.Services.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<UpdateTrustStore>().ProvisionAsync(rootMetadata, token);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
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
            var monitoringPaths = new[] { "/api/v1/agent-packages", $"/api/v1/devices/{device.Id:D}/agent-status", $"/api/v1/devices/{device.Id:D}/agent-settings", $"/api/v1/devices/{device.Id:D}/agent-settings-status", $"/api/v1/devices/{device.Id:D}/agent-queue-capacity", $"/api/v1/devices/{device.Id:D}/agent-queue-capacity-status", $"/api/v1/devices/{device.Id:D}/host-metrics?from=2026-11-01T00:00:00Z&until=2026-11-02T00:00:00Z", $"/api/v1/devices/{device.Id:D}/host-logs?from=2026-11-01T00:00:00Z&until=2026-11-02T00:00:00Z" };
            foreach (var path in monitoringPaths)
            {
                using var anonymousRead = await agent.GetAsync(path, token);
                Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousRead.StatusCode, path);
                using var apiRead = await api.GetAsync(path, token);
                Assert.AreEqual(HttpStatusCode.OK, apiRead.StatusCode, path);
            }
            var aggregatePath = $"/api/v1/devices/{device.Id:D}/host-metrics/aggregate?agentId={Guid.CreateVersion7():D}&from=2026-11-01T00:00:00Z&until=2026-11-02T00:00:00Z&kind=CpuTotalPercent&source=test&bucketSeconds=300";
            using (var anonymousAggregate = await agent.GetAsync(aggregatePath, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousAggregate.StatusCode);
            using (var apiAggregate = await api.GetAsync(aggregatePath, token)) Assert.AreEqual(HttpStatusCode.NotFound, apiAggregate.StatusCode);
            using (var anonymousStatus = await agent.GetAsync($"/api/v1/devices/{device.Id:D}/agent-status", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, anonymousStatus.StatusCode);
            using (var apiStatus = await api.GetAsync($"/api/v1/devices/{device.Id:D}/agent-status", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, apiStatus.StatusCode);
                Assert.IsTrue(apiStatus.Headers.CacheControl?.NoStore);
                Assert.AreEqual(AgentConnectionState.NotRegistered, (await apiStatus.Content.ReadFromJsonAsync<AgentConnectionStatus>(cancellationToken: token))!.State);
            }
            using (var apiCannotImpersonate = await api.GetAsync("/api/v1/agent/identity", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, apiCannotImpersonate.StatusCode);
            using (var apiCannotReadUpdateMetadata = await api.GetAsync("/api/v1/agent/update/metadata/timestamp.json", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, apiCannotReadUpdateMetadata.StatusCode);
            using (var invalidPackagePage = await api.GetAsync("/api/v1/agent-packages?page=0", token)) Assert.AreEqual(HttpStatusCode.BadRequest, invalidPackagePage.StatusCode);
            using (var apiCannotAudit = await api.GetAsync("/api/v1/operations/audit?from=2026-11-01T00:00:00Z&to=2026-11-02T00:00:00Z", token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, apiCannotAudit.StatusCode);
            using (var apiCannotHeartbeat = await api.PostAsync("/api/v1/agent/heartbeat", null, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, apiCannotHeartbeat.StatusCode);
            using (var apiCannotReadMachineSettings = await api.GetAsync("/api/v1/agent/settings", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, apiCannotReadMachineSettings.StatusCode);
            using (var apiCannotReadMachineCapacity = await api.GetAsync("/api/v1/agent/queue-capacity", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, apiCannotReadMachineCapacity.StatusCode);
            using (var apiCannotBatch = await api.PostAsync("/api/v1/agent/batches", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), token)) Assert.AreEqual(HttpStatusCode.Unauthorized, apiCannotBatch.StatusCode);
            using (var apiCannotDiscover = await api.GetAsync("/api/v1/service-discovery/candidates/", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, apiCannotDiscover.StatusCode);
            using (var apiCannotApprove = await api.PostAsync($"/api/v1/service-discovery/candidates/{Guid.CreateVersion7():D}/approve", null, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, apiCannotApprove.StatusCode);
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
                using var packages = await reader.GetAsync("/api/v1/agent-packages", token);
                Assert.AreEqual(HttpStatusCode.OK, packages.StatusCode);
                Assert.IsTrue(packages.Headers.CacheControl?.NoStore);
                Assert.IsEmpty((await packages.Content.ReadFromJsonAsync<AgentPackagePage>(cancellationToken: token))!.Items);
                using var status = await reader.GetAsync($"/api/v1/devices/{device.Id:D}/agent-status", token);
                Assert.AreEqual(HttpStatusCode.OK, status.StatusCode);
                using var logHistory = await reader.GetAsync($"/api/v1/devices/{device.Id:D}/host-logs?from=2026-11-01T00:00:00Z&until=2026-11-02T00:00:00Z", token);
                Assert.AreEqual(HttpStatusCode.OK, logHistory.StatusCode);
                Assert.IsTrue(logHistory.Headers.CacheControl?.NoStore);
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
                using var readCapacity = await reader.GetAsync($"/api/v1/devices/{device.Id:D}/agent-queue-capacity", token);
                Assert.AreEqual(HttpStatusCode.OK, readCapacity.StatusCode);
                using var manageCapacity = await reader.GetAsync("/api/v1/agent-management/queue-capacity/platform", token);
                Assert.AreEqual(HttpStatusCode.Forbidden, manageCapacity.StatusCode);
                using var manageReplay = await reader.GetAsync("/api/v1/agent-management/replay-policy/", token);
                Assert.AreEqual(HttpStatusCode.Forbidden, manageReplay.StatusCode);
                using var changeCapacity = await reader.PutAsJsonAsync("/api/v1/agent-management/queue-capacity/platform", new AgentQueueCapacityUpdate(new(1048576, 100), ""), token);
                Assert.AreEqual(HttpStatusCode.Forbidden, changeCapacity.StatusCode);
                using var details = await reader.GetAsync($"/devices/{device.Id:D}", token);
                var detailsDoc = await new HtmlParser().ParseDocumentAsync(await details.Content.ReadAsStringAsync(token), token);
                Assert.Contains("Agent 狀態", detailsDoc.Body!.TextContent);
                Assert.Contains("未註冊或已撤銷", detailsDoc.Body.TextContent);
                Assert.Contains("尚無佇列回報", detailsDoc.Body.TextContent);
                Assert.IsNull(detailsDoc.QuerySelector($"a[href='/devices/{device.Id:D}/agent']"));
                Assert.IsNull(detailsDoc.QuerySelector($"a[href='/devices/{device.Id:D}/log-sources']"));
                using var deniedLogPage = await reader.GetAsync($"/devices/{device.Id:D}/log-sources", token);
                Assert.AreEqual(HttpStatusCode.Redirect, deniedLogPage.StatusCode);
                Assert.AreEqual("/account/access-denied", deniedLogPage.Headers.Location!.AbsolutePath);
                using var denied = await reader.PostAsync($"/api/v1/agent-management/devices/{device.Id:D}/enrollments", null, token);
                Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            }
            var logPagePath = $"/devices/{device.Id:D}/log-sources";
            using (var forgedLogCreate = await admin.PostAsync(logPagePath, new FormUrlEncodedContent(new Dictionary<string, string>
            { ["_handler"] = "log-source-create", ["Input.Name"] = "forged", ["Input.Kind"] = "WindowsEvent", ["Input.Target"] = "System" }), token))
                Assert.AreEqual(HttpStatusCode.BadRequest, forgedLogCreate.StatusCode);
            using (var page = await admin.GetAsync(logPagePath, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
                var doc = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token);
                var form = doc.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "log-source-create");
                var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
                fields["Input.Name"] = "Application"; fields["Input.Kind"] = "WindowsEvent"; fields["Input.Target"] = "Application";
                fields["Input.TextMode"] = "SingleLine"; fields["Input.Enabled"] = "true";
                using var post = await admin.PostAsync(logPagePath, new FormUrlEncodedContent(fields), token);
                Assert.AreEqual(HttpStatusCode.Redirect, post.StatusCode);
                Assert.AreEqual(logPagePath, post.Headers.Location!.AbsolutePath);
            }
            var source = (await (await admin.GetAsync($"/api/v1/agent-management/devices/{device.Id:D}/log-sources/", token)).Content.ReadFromJsonAsync<HostLogSourceView[]>(cancellationToken: token))!.Single();
            Assert.AreEqual("Application", source.Settings.Name);
            var editLogPath = $"{logPagePath}/{source.Settings.SourceId:D}";
            using (var page = await admin.GetAsync(editLogPath, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
                var doc = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token);
                var retireForm = doc.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "log-source-retire");
                var retireFields = retireForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
                using var unconfirmed = await admin.PostAsync(editLogPath, new FormUrlEncodedContent(retireFields), token);
                doc = await new HtmlParser().ParseDocumentAsync(await unconfirmed.Content.ReadAsStringAsync(token), token);
                Assert.Contains("請先確認移除", doc.Body!.TextContent);
                var form = doc.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "log-source-edit");
                var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
                fields["Input.Name"] = "Renamed"; fields["Input.Enabled"] = "true";
                using var post = await admin.PostAsync(editLogPath, new FormUrlEncodedContent(fields), token);
                Assert.AreEqual(HttpStatusCode.Redirect, post.StatusCode);
                Assert.AreEqual(logPagePath, post.Headers.Location!.AbsolutePath);
            }
            var updatedSource = (await (await admin.GetAsync($"/api/v1/agent-management/devices/{device.Id:D}/log-sources/", token)).Content.ReadFromJsonAsync<HostLogSourceView[]>(cancellationToken: token))!.Single();
            Assert.AreEqual("Renamed", updatedSource.Settings.Name);
            using (var page = await admin.GetAsync(editLogPath, token))
            {
                var doc = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token);
                var form = doc.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "log-source-edit");
                var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
                fields["Input.Version"] = source.Version; fields["Input.Name"] = "Stale"; fields["Input.Enabled"] = "true";
                using var post = await admin.PostAsync(editLogPath, new FormUrlEncodedContent(fields), token);
                var conflictDoc = await new HtmlParser().ParseDocumentAsync(await post.Content.ReadAsStringAsync(token), token);
                Assert.Contains("來源已變更或不存在", conflictDoc.Body!.TextContent);
            }
            using (var page = await admin.GetAsync(editLogPath, token))
            {
                var doc = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token);
                var form = doc.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "log-source-retire");
                var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
                fields["Retire.Confirm"] = "true";
                using var post = await admin.PostAsync(editLogPath, new FormUrlEncodedContent(fields), token);
                Assert.AreEqual(HttpStatusCode.Redirect, post.StatusCode);
                Assert.AreEqual(logPagePath, post.Headers.Location!.AbsolutePath);
                using var repeated = await admin.PostAsync(editLogPath, new FormUrlEncodedContent(fields), token);
                Assert.AreEqual(HttpStatusCode.OK, repeated.StatusCode);
                var repeatedDoc = await new HtmlParser().ParseDocumentAsync(await repeated.Content.ReadAsStringAsync(token), token);
                Assert.Contains("找不到來源", repeatedDoc.Body!.TextContent);
            }
            var retiredSources = await (await admin.GetAsync($"/api/v1/agent-management/devices/{device.Id:D}/log-sources/", token)).Content.ReadFromJsonAsync<HostLogSourceView[]>(cancellationToken: token);
            Assert.IsEmpty(retiredSources!);
            using (var forged = await admin.PostAsync($"/api/v1/agent-management/devices/{device.Id:D}/enrollments", null, token))
                Assert.AreEqual(HttpStatusCode.BadRequest, forged.StatusCode);
            using (var forgedSettings = await admin.PutAsJsonAsync("/api/v1/agent-management/settings/platform", new AgentSettingsUpdate(new(60, null, null), ""), token))
                Assert.AreEqual(HttpStatusCode.BadRequest, forgedSettings.StatusCode);
            using (var forgedPackageSource = await admin.PutAsJsonAsync("/api/v1/agent-management/package-source/",
                new AgentPackageSourceUpdate("https://packages.example.test/", PackageCredentialAction.Keep, null, ""), token))
                Assert.AreEqual(HttpStatusCode.BadRequest, forgedPackageSource.StatusCode);
            using (var forgedTrust = await admin.PostAsJsonAsync("/api/v1/agent-management/package-source/trust",
                new AgentPackageTrustRequest(rootMetadata, new string('0', 64)), token))
                Assert.AreEqual(HttpStatusCode.BadRequest, forgedTrust.StatusCode);
            using (var forgedCache = await admin.PostAsync($"/api/v1/agent-packages/{Guid.CreateVersion7():D}/cache", null, token))
                Assert.AreEqual(HttpStatusCode.BadRequest, forgedCache.StatusCode);
            using (var upload = new MultipartFormDataContent())
            {
                upload.Add(new ByteArrayContent(rootMetadata), "root", "root.json");
                upload.Add(new StringContent(new string('0', 64)), "sha256");
                using var forgedUpload = await admin.PostAsync("/settings/agent-packages/trust", upload, token);
                Assert.AreEqual(HttpStatusCode.BadRequest, forgedUpload.StatusCode);
            }
            using (var forgedCapacity = await admin.PutAsJsonAsync("/api/v1/agent-management/queue-capacity/platform", new AgentQueueCapacityUpdate(new(1048576, 100), ""), token))
                Assert.AreEqual(HttpStatusCode.BadRequest, forgedCapacity.StatusCode);
            using (var replayRead = await admin.GetAsync("/api/v1/agent-management/replay-policy/", token))
            {
                var replay = await replayRead.Content.ReadFromJsonAsync<AgentReplayPolicyView>(cancellationToken: token);
                Assert.AreEqual(AgentReplayPhase.Disabled, replay!.Phase);
                using var forgedReplay = await admin.PutAsJsonAsync("/api/v1/agent-management/replay-policy/", new AgentReplayPolicyUpdate(AgentReplayPhase.Grace, replay.Version, true), token);
                Assert.AreEqual(HttpStatusCode.BadRequest, forgedReplay.StatusCode);
            }
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
            using (var replayRead = await admin.GetAsync("/api/v1/agent-management/replay-policy/", token))
            {
                var replay = (await replayRead.Content.ReadFromJsonAsync<AgentReplayPolicyView>(cancellationToken: token))!;
                using var unconfirmed = await admin.PutAsJsonAsync("/api/v1/agent-management/replay-policy/", new AgentReplayPolicyUpdate(AgentReplayPhase.Grace, replay.Version, false), token);
                Assert.AreEqual(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
                using var activated = await admin.PutAsJsonAsync("/api/v1/agent-management/replay-policy/", new AgentReplayPolicyUpdate(AgentReplayPhase.Grace, replay.Version, true), token);
                Assert.AreEqual(HttpStatusCode.OK, activated.StatusCode);
                var grace = (await activated.Content.ReadFromJsonAsync<AgentReplayPolicyView>(cancellationToken: token))!;
                Assert.AreEqual(AgentReplayPhase.Grace, grace.Phase);
                using var stale = await admin.PutAsJsonAsync("/api/v1/agent-management/replay-policy/", new AgentReplayPolicyUpdate(AgentReplayPhase.Grace, replay.Version, true), token);
                Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
                var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
                var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
                using var audit = await admin.GetAsync($"/api/v1/operations/audit/agent-replay?from={from}&to={to}", token);
                Assert.AreEqual(HttpStatusCode.OK, audit.StatusCode);
                var records = (await audit.Content.ReadFromJsonAsync<OperationAuditPage>(cancellationToken: token))!;
                Assert.HasCount(1, records.Items);
                Assert.AreEqual(AgentReplayPhase.Grace, records.Items[0].AgentReplayPolicy!.Current);
                using var speedAudit = await admin.GetAsync($"/api/v1/operations/audit?from={from}&to={to}", token);
                Assert.HasCount(0, (await speedAudit.Content.ReadFromJsonAsync<OperationAuditPage>(cancellationToken: token))!.Items);
                using var settingsPage = await admin.GetAsync("/settings/agents", token);
                Assert.AreEqual(HttpStatusCode.OK, settingsPage.StatusCode);
                var settingsDoc = await new HtmlParser().ParseDocumentAsync(await settingsPage.Content.ReadAsStringAsync(token), token);
                Assert.Contains("過渡期間", settingsDoc.Body!.TextContent);
                Assert.Contains("啟用 24 小時期限", settingsDoc.Body.TextContent);
                var replayForm = settingsDoc.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "agent-replay");
                var fields = replayForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
                fields["ReplayInput.Confirmed"] = "true";
                using var earlyEnforcement = await admin.PostAsync("/settings/agents", new FormUrlEncodedContent(fields), token);
                var earlyDoc = await new HtmlParser().ParseDocumentAsync(await earlyEnforcement.Content.ReadAsStringAsync(token), token);
                Assert.Contains("過渡期尚未結束", earlyDoc.Body!.TextContent);
                using var auditPage = await admin.GetAsync("/settings/audit?source=agent-replay", token);
                Assert.AreEqual(HttpStatusCode.OK, auditPage.StatusCode);
                var auditDoc = await new HtmlParser().ParseDocumentAsync(await auditPage.Content.ReadAsStringAsync(token), token);
                Assert.Contains("Agent 補送政策", auditDoc.Body!.TextContent);
                Assert.Contains("過渡期間", auditDoc.Body.TextContent);
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
            using (var saveCapacity = await admin.PutAsJsonAsync($"/api/v1/agent-management/queue-capacity/devices/{device.Id:D}", new AgentQueueCapacityUpdate(new(1048576, 100), ""), token))
                Assert.AreEqual(HttpStatusCode.OK, saveCapacity.StatusCode);
            using (var staleCapacity = await admin.PutAsJsonAsync($"/api/v1/agent-management/queue-capacity/devices/{device.Id:D}", new AgentQueueCapacityUpdate(new(1048576, 200), ""), token))
                Assert.AreEqual(HttpStatusCode.Conflict, staleCapacity.StatusCode);
            using (var invalidCapacity = await admin.PutAsJsonAsync($"/api/v1/agent-management/queue-capacity/devices/{device.Id:D}", new AgentQueueCapacityUpdate(new(1, 100), ""), token))
                Assert.AreEqual(HttpStatusCode.BadRequest, invalidCapacity.StatusCode);
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
            using (var aggregate = await api.GetAsync($"/api/v1/devices/{device.Id:D}/host-metrics/aggregate?agentId={result.AgentId:D}&from=2026-11-01T00:00:00Z&until=2026-11-02T00:00:00Z&kind=CpuTotalPercent&source=test&bucketSeconds=300", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, aggregate.StatusCode);
                var body = await aggregate.Content.ReadFromJsonAsync<HostMetricAggregate>(cancellationToken: token);
                Assert.IsNotNull(body);
                Assert.IsEmpty(body.Buckets);
            }
            using (var invalidAggregate = await api.GetAsync($"/api/v1/devices/{device.Id:D}/host-metrics/aggregate?agentId={result.AgentId:D}&from=2026-11-01T00:00:00Z&until=2026-11-02T00:00:00Z&kind=CpuTotalPercent&source=test&bucketSeconds=0", token))
                Assert.AreEqual(HttpStatusCode.BadRequest, invalidAggregate.StatusCode);
            foreach (var path in new[] { $"/devices/{device.Id:D}", $"/api/v1/devices/{device.Id:D}/agent-status" })
            {
                using var visible = await reader.GetAsync(path, token);
                Assert.AreEqual(HttpStatusCode.OK, visible.StatusCode);
                var content = await visible.Content.ReadAsStringAsync(token);
                Assert.IsFalse(content.Contains(enrollment.Secret, StringComparison.Ordinal), "Enrollment secret leaked to the reader.");
                Assert.IsFalse(content.Contains(machineSecret, StringComparison.Ordinal), "Machine secret leaked to the reader.");
            }
            factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AgentSecretTest").LogWarning("Capture marker");
            var logs = captured.Messages.ToArray();
            Assert.IsTrue(logs.Any(message => message.Contains("Capture marker", StringComparison.Ordinal)));
            Assert.IsFalse(logs.Any(message => message.Contains(enrollment.Secret, StringComparison.Ordinal)), "Enrollment secret leaked to logs.");
            Assert.IsFalse(logs.Any(message => message.Contains(machineSecret, StringComparison.Ordinal)), "Machine secret leaked to logs.");
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
            using (var missingMetadata = await agent.GetAsync("/api/v1/agent/update/metadata/timestamp.json", token)) Assert.AreEqual(HttpStatusCode.NotFound, missingMetadata.StatusCode);
            using (var forbiddenCatalogue = await agent.GetAsync("/api/v1/agent/update/metadata/catalog.json", token)) Assert.AreEqual(HttpStatusCode.NotFound, forbiddenCatalogue.StatusCode);
            using (var rootResponse = await agent.GetAsync("/api/v1/agent/update/metadata/1.root.json", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, rootResponse.StatusCode);
                Assert.IsTrue(rootResponse.Headers.CacheControl?.NoStore);
                CollectionAssert.AreEqual(rootMetadata, await rootResponse.Content.ReadAsByteArrayAsync(token));
            }
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
                await db.UpdateRoots.ExecuteDeleteAsync(token);
            using (var unavailableRoot = await agent.GetAsync("/api/v1/agent/update/metadata/1.root.json", token))
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, unavailableRoot.StatusCode);
            using (var readerSource = await reader.GetAsync("/api/v1/agent-management/package-source/", token)) Assert.AreEqual(HttpStatusCode.Forbidden, readerSource.StatusCode);
            using (var readerTrust = await reader.GetAsync("/api/v1/agent-management/package-source/trust", token)) Assert.AreEqual(HttpStatusCode.Forbidden, readerTrust.StatusCode);
            using (var machineTrust = await agent.GetAsync("/api/v1/agent-management/package-source/trust", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, machineTrust.StatusCode);
            using (var readerCache = await reader.PostAsync($"/api/v1/agent-packages/{Guid.CreateVersion7():D}/cache", null, token)) Assert.AreEqual(HttpStatusCode.Forbidden, readerCache.StatusCode);
            using (var machineCache = await agent.PostAsync($"/api/v1/agent-packages/{Guid.CreateVersion7():D}/cache", null, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, machineCache.StatusCode);
            using (var currentTrust = await admin.GetAsync("/api/v1/agent-management/package-source/trust", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, currentTrust.StatusCode);
                Assert.IsTrue(currentTrust.Headers.CacheControl?.NoStore);
                var trust = await currentTrust.Content.ReadFromJsonAsync<AgentPackageTrustView>(token);
                Assert.IsNotNull(trust);
                Assert.IsTrue(trust.Initialized);
                Assert.AreEqual(1, trust.RootVersion);
            }
            using (var apiSource = await api.GetAsync("/api/v1/agent-management/package-source/", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, apiSource.StatusCode);
            using (var machineSource = await agent.GetAsync("/api/v1/agent-management/package-source/", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, machineSource.StatusCode);
            using (var unsetSource = await admin.GetAsync("/api/v1/agent-management/package-source/", token)) Assert.AreEqual(HttpStatusCode.NoContent, unsetSource.StatusCode);
            const string packageSecret = "write-only-package-secret";
            using (var savedSource = await admin.PutAsJsonAsync("/api/v1/agent-management/package-source/",
                new AgentPackageSourceUpdate("https://packages.example.test/", PackageCredentialAction.Replace, packageSecret, ""), token))
            {
                Assert.AreEqual(HttpStatusCode.OK, savedSource.StatusCode);
                Assert.IsTrue(savedSource.Headers.CacheControl?.NoStore);
                Assert.DoesNotContain(packageSecret, await savedSource.Content.ReadAsStringAsync(token));
            }
            using (var readSource = await admin.GetAsync("/api/v1/agent-management/package-source/", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, readSource.StatusCode);
                Assert.IsTrue(readSource.Headers.CacheControl?.NoStore);
                var content = await readSource.Content.ReadAsStringAsync(token);
                Assert.DoesNotContain(packageSecret, content);
                Assert.IsTrue(JsonSerializer.Deserialize<AgentPackageSourceView>(content, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.HasCredential);
            }
            using (var forbiddenPage = await reader.GetAsync("/settings/agent-packages", token))
            {
                Assert.AreEqual(HttpStatusCode.Redirect, forbiddenPage.StatusCode);
                Assert.Contains("access-denied", forbiddenPage.Headers.Location!.OriginalString, StringComparison.OrdinalIgnoreCase);
            }
            using (var packagePage = await admin.GetAsync("/settings/agent-packages", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, packagePage.StatusCode);
                var html = await packagePage.Content.ReadAsStringAsync(token);
                Assert.DoesNotContain(packageSecret, html);
                var doc = await new HtmlParser().ParseDocumentAsync(html, token);
                var form = doc.QuerySelectorAll("form").Single(e => e.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "package-source");
                var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(e => e.GetAttribute("name")!, e => e.GetAttribute("value") ?? "");
                fields["SourceInput.Action"] = "0";
                fields["SourceInput.Credential"] = "";
                fields["SourceInput.BaseUri"] = "http://packages.example.test/";
                using (var invalidSource = await admin.PostAsync("/settings/agent-packages", new FormUrlEncodedContent(fields), token))
                {
                    var invalidDocument = await new HtmlParser().ParseDocumentAsync(await invalidSource.Content.ReadAsStringAsync(token), token);
                    Assert.Contains("請確認來源網址", invalidDocument.Body!.TextContent);
                    Assert.IsTrue(invalidDocument.QuerySelector("form.package-form")!.ParentElement!.HasAttribute("open"));
                    Assert.AreEqual("http://packages.example.test/", invalidDocument.QuerySelector("#package-origin")!.GetAttribute("value"));
                }
                fields["SourceInput.BaseUri"] = "https://packages.example.test/";
                using var saved = await admin.PostAsync("/settings/agent-packages", new FormUrlEncodedContent(fields), token);
                Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
                var savedHtml = await saved.Content.ReadAsStringAsync(token);
                Assert.Contains("來源已儲存", (await new HtmlParser().ParseDocumentAsync(savedHtml, token)).Body!.TextContent);
                Assert.DoesNotContain(packageSecret, savedHtml);
            }
            var cachedPackageId = Guid.CreateVersion7();
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
            {
                db.Packages.Add(new AgentPackage { Id = cachedPackageId, Version = "1.0.0", RuntimeIdentifier = "win-x64", Size = 1,
                    Sha256 = new string('a', 64), ManifestSha256 = new string('b', 64), Listed = true, VerifiedUntil = clock.Now.AddHours(1) });
                await db.SaveChangesAsync(token);
            }
            using (var emptyPage = await admin.GetAsync("/settings/agent-packages?page=2", token))
            {
                var html = await new HtmlParser().ParseDocumentAsync(await emptyPage.Content.ReadAsStringAsync(token), token);
                Assert.Contains("此頁沒有套件版本", html.Body!.TextContent);
                Assert.IsNotNull(html.QuerySelector("a[href='?page=1']"));
            }
            using (var beforeRevocation = await admin.GetAsync("/settings/agent-packages", token))
            {
                var html = await new HtmlParser().ParseDocumentAsync(await beforeRevocation.Content.ReadAsStringAsync(token), token);
                var form = html.QuerySelectorAll("form").Single(e => e.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "package-cache");
                var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(e => e.GetAttribute("name")!, e => e.GetAttribute("value") ?? "");
                fields["CacheInput.Id"] = cachedPackageId.ToString("D");
                await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
                    await db.Packages.Where(x => x.Id == cachedPackageId).ExecuteUpdateAsync(x => x.SetProperty(p => p.Revoked, true), token);
                using var stale = await admin.PostAsync("/settings/agent-packages", new FormUrlEncodedContent(fields), token);
                Assert.AreEqual(HttpStatusCode.OK, stale.StatusCode);
                var staleDocument = await new HtmlParser().ParseDocumentAsync(await stale.Content.ReadAsStringAsync(token), token);
                Assert.Contains("套件或來源狀態已變更", staleDocument.Body!.TextContent);
            }
            Assert.IsFalse(captured.Messages.Any(message => message.Contains(packageSecret, StringComparison.Ordinal)));
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
            var lossReport = new AgentMetricLossReport(Guid.CreateVersion7(), clock.Now, 2, 7, true);
            var queueReport = new AgentHeartbeatReport(1, queueEpoch, 1, new AgentQueueSnapshot(1234, 5, 2, 8192, 10), settingsReport,
                new AgentQuarantineBreakdown(0, 0, 2, 0, 0, 0, 0), MetricLoss: lossReport);
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
            using (var invalid = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", queueReport with { Sequence = 2, Quarantine = queueReport.Quarantine! with { Conflict = -1 } }, token))
                Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            using (var invalid = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", queueReport with { Sequence = 2, Queue = null }, token))
                Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            using (var invalid = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", queueReport with { Sequence = 2, MetricLoss = lossReport with { Records = -1 } }, token))
                Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            using (var statusResponse = await api.GetAsync($"/api/v1/devices/{device.Id:D}/agent-status", token))
            {
                var status = await statusResponse.Content.ReadFromJsonAsync<AgentConnectionStatus>(cancellationToken: token);
                Assert.AreEqual(queueReport.Queue, status!.Queue!.Snapshot);
                Assert.AreEqual(queueReport.Quarantine, status.Queue.Quarantine);
                Assert.AreEqual(lossReport, status.MetricLoss!.Snapshot);
            }
            using (var wrongVersion = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", queueReport with { Version = 2 }, token))
                Assert.AreEqual(HttpStatusCode.BadRequest, wrongVersion.StatusCode);
            using (var duplicate = await agent.PostAsync("/api/v1/agent/heartbeat", JsonBytes("{\"version\":1,\"version\":1,\"queue\":null}"u8.ToArray()), token))
                Assert.AreEqual(HttpStatusCode.BadRequest, duplicate.StatusCode);
            using (var oversized = await agent.PostAsync("/api/v1/agent/heartbeat", JsonBytes(new byte[8193]), token))
                Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
            using (var oversized = await agent.PostAsync("/api/v1/agent/heartbeat", new UnknownLengthJsonContent(8193), token))
                Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
            var now = DateTimeOffset.UtcNow;
            var observation = new AgentRuntimeMessage(Guid.CreateVersion7(), AgentBatchCodec.RuntimeKind, now, "test operating system", "X64", ".NET 10");
            var batch = new AgentBatch(1, Guid.CreateVersion7(), now, [observation]);
            var batchBytes = AgentBatchCodec.Serialize(batch);
            using (var batchResponse = await agent.PostAsync("/api/v1/agent/batches", JsonBytes(batchBytes), token)) Assert.AreEqual(HttpStatusCode.NoContent, batchResponse.StatusCode);
            await using (var db = await resources.CreateDbContextAsync(token))
            {
                var renamed = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
                renamed.Update("renamed server", "192.0.2.45", true, true, clock.Now);
                await db.SaveChangesAsync(token);
            }
            using (var afterDeviceChange = await agent.GetAsync("/api/v1/agent/identity", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, afterDeviceChange.StatusCode);
                var identity = await afterDeviceChange.Content.ReadFromJsonAsync<AgentIdentityResponse>(cancellationToken: token);
                Assert.AreEqual(result.AgentId, identity!.AgentId);
                Assert.AreEqual(device.Id, identity.DeviceId);
            }
            var afterChange = batch with { BatchId = Guid.CreateVersion7(), Items = [observation with { MessageId = Guid.CreateVersion7() }] };
            using (var afterDeviceChange = await agent.PostAsync("/api/v1/agent/batches", JsonBytes(AgentBatchCodec.Serialize(afterChange)), token))
                Assert.AreEqual(HttpStatusCode.NoContent, afterDeviceChange.StatusCode);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
            {
                Assert.AreEqual(1, await db.Agents.CountAsync(x => x.DeviceId == device.Id, token));
                Assert.IsTrue(await db.ReceivedBatches.AnyAsync(x => x.AgentId == result.AgentId && x.BatchId == afterChange.BatchId, token));
            }
            using (var batchRetry = await agent.PostAsync("/api/v1/agent/batches", JsonBytes(batchBytes), token)) Assert.AreEqual(HttpStatusCode.NoContent, batchRetry.StatusCode);
            using (var compressedRetry = await agent.PostAsync("/api/v1/agent/batches", GzipBytes(batchBytes), token)) Assert.AreEqual(HttpStatusCode.NoContent, compressedRetry.StatusCode);
            using (var changedBatch = await agent.PostAsync("/api/v1/agent/batches", JsonBytes(AgentBatchCodec.Serialize(batch with { Items = [observation with { RuntimeVersion = "changed" }] })), token))
                Assert.AreEqual(HttpStatusCode.Conflict, changedBatch.StatusCode);
            using (var invalidBatch = await agent.PostAsync("/api/v1/agent/batches", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), token))
            {
                Assert.AreEqual(HttpStatusCode.BadRequest, invalidBatch.StatusCode);
                Assert.IsTrue(invalidBatch.Headers.TryGetValues(AgentMetricBatchCodec.SupportedSchemaHeader, out var supported));
                Assert.Contains("2", string.Join(",", supported));
            }
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
            using (var machineCannotDiscover = await agent.GetAsync("/api/v1/service-discovery/candidates/", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, machineCannotDiscover.StatusCode);
            using (var machineCannotApprove = await agent.PostAsync($"/api/v1/service-discovery/candidates/{Guid.CreateVersion7():D}/approve", null, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, machineCannotApprove.StatusCode);
            using (var connected = await api.GetAsync($"/api/v1/devices/{device.Id:D}/agent-status", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, connected.StatusCode);
                var status = await connected.Content.ReadFromJsonAsync<AgentConnectionStatus>(cancellationToken: token);
                Assert.AreEqual(AgentConnectionState.Online, status!.State);
                Assert.AreEqual(queueReport.Queue, status.Queue!.Snapshot);
                Assert.AreEqual(queueReport.Quarantine, status.Queue.Quarantine);
                Assert.IsNotNull(status.LastHeartbeatAt);
                Assert.IsNotNull(status.LastCommittedBatchAt);
                Assert.AreEqual(2L, status.ReceiptCapacity!.RetainedBatches);
                Assert.AreEqual(batchBytes.Length + AgentBatchCodec.Serialize(afterChange).Length, status.ReceiptCapacity.OriginalPayloadBytes);
            }
            using (var deliveryPage = await admin.GetAsync($"/devices/{device.Id:D}", token))
            {
                var page = await new HtmlParser().ParseDocumentAsync(await deliveryPage.Content.ReadAsStringAsync(token), token);
                var fields = page.QuerySelectorAll("dl div").Where(x => x.QuerySelector("dt") is not null)
                    .ToDictionary(x => x.QuerySelector("dt")!.TextContent, x => x.QuerySelector("dd")?.TextContent.Trim() ?? "");
                Assert.AreEqual("3 批", fields["等待傳送"]);
                Assert.AreEqual("2 批", fields["已隔離"]);
                Assert.AreEqual("5 / 10 批", fields["批次容量"]);
                Assert.AreNotEqual("保留範圍內無資料", fields["目前 Agent 最後提交批次（UTC）"]);
                Assert.Contains("不代表 Agent 已收到確認", page.Body!.TextContent);
                Assert.Contains("批次內容衝突：2 批", page.Body.TextContent);
                Assert.Contains("可能不是目前狀態", page.Body!.TextContent);
                Assert.Contains("至少 7 筆指標（2 批）", page.Body.TextContent);
                Assert.Contains("部分資料的完整性無法確認", page.Body.TextContent);
            }
            using (var deliveryPage = await reader.GetAsync($"/devices/{device.Id:D}", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, deliveryPage.StatusCode);
                var page = await new HtmlParser().ParseDocumentAsync(await deliveryPage.Content.ReadAsStringAsync(token), token);
                Assert.Contains("等待傳送", page.Body!.TextContent);
                Assert.Contains("3 批", page.Body.TextContent);
                Assert.Contains("中央收據容量", page.Body.TextContent);
                Assert.Contains("不代表 SQL 磁碟用量", page.Body.TextContent);
                Assert.Contains("至少 7 筆指標（2 批）", page.Body.TextContent);
                Assert.DoesNotContain("入列時間，比平台記錄的接收時間超前", page.Body.TextContent);
            }
            var futureBatch = batch with { BatchId = Guid.CreateVersion7(), EnqueuedAt = clock.Now.AddMinutes(6), Items = [observation with { MessageId = Guid.CreateVersion7() }] };
            using (var futureResult = await agent.PostAsync("/api/v1/agent/batches", JsonBytes(AgentBatchCodec.Serialize(futureBatch)), token))
                Assert.AreEqual(HttpStatusCode.NoContent, futureResult.StatusCode);
            using (var futureStatus = await reader.GetAsync($"/api/v1/devices/{device.Id:D}/agent-status", token))
            {
                var status = await futureStatus.Content.ReadFromJsonAsync<AgentConnectionStatus>(cancellationToken: token);
                Assert.IsTrue(status!.LastBatchTime!.EnqueuedAheadOfReceipt);
                Assert.AreEqual(futureBatch.EnqueuedAt, status.LastBatchTime.EnqueuedAt);
            }
            using (var futurePage = await reader.GetAsync($"/devices/{device.Id:D}", token))
            {
                var page = await new HtmlParser().ParseDocumentAsync(await futurePage.Content.ReadAsStringAsync(token), token);
                Assert.Contains("入列時間，比平台記錄的接收時間超前超過 5 分鐘", page.Body!.TextContent);
                Assert.Contains("歷史批次觀察，不代表目前時鐘狀態", page.Body.TextContent);
            }
            clock.Now += TimeSpan.FromMinutes(4);
            using (var heartbeat = await agent.PostAsync("/api/v1/agent/heartbeat", null, token))
                Assert.AreEqual(HttpStatusCode.NoContent, heartbeat.StatusCode);
            using (var deliveryPage = await reader.GetAsync($"/devices/{device.Id:D}", token))
            {
                var page = await new HtmlParser().ParseDocumentAsync(await deliveryPage.Content.ReadAsStringAsync(token), token);
                Assert.Contains("佇列回報已過時或時間異常", page.Body!.TextContent);
                Assert.Contains("統計已過時或平台時間異常", page.Body.TextContent);
                Assert.Contains("已連線", page.Body.TextContent);
            }
            using (var full = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", queueReport with { Sequence = 2, Queue = queueReport.Queue! with { TotalBatches = 10 } }, token))
                Assert.AreEqual(HttpStatusCode.NoContent, full.StatusCode);
            using (var deliveryPage = await reader.GetAsync($"/devices/{device.Id:D}", token))
            {
                var page = await new HtmlParser().ParseDocumentAsync(await deliveryPage.Content.ReadAsStringAsync(token), token);
                Assert.Contains("佇列已達容量", page.Body!.TextContent);
            }
            using (var unreadable = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", queueReport with { Sequence = 3, Queue = null, Quarantine = null }, token))
                Assert.AreEqual(HttpStatusCode.NoContent, unreadable.StatusCode);
            using (var deliveryPage = await admin.GetAsync($"/devices/{device.Id:D}", token))
            {
                var page = await new HtmlParser().ParseDocumentAsync(await deliveryPage.Content.ReadAsStringAsync(token), token);
                Assert.Contains("Agent 無法讀取佇列，資料狀態未知", page.Body!.TextContent);
            }
            using (var legacy = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", queueReport with { Sequence = 4, Quarantine = null }, token))
                Assert.AreEqual(HttpStatusCode.NoContent, legacy.StatusCode);
            using (var deliveryPage = await reader.GetAsync($"/devices/{device.Id:D}", token))
            {
                var page = await new HtmlParser().ParseDocumentAsync(await deliveryPage.Content.ReadAsStringAsync(token), token);
                Assert.Contains("隔離原因摘要尚未提供", page.Body!.TextContent);
                Assert.DoesNotContain("批次內容衝突：2 批", page.Body.TextContent);
            }
            // Synthetic receipt lengths exercise the logical byte limit without transferring 4 GiB.
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
            {
                for (var i = 0; i < 256; i++)
                    db.ReceivedBatches.Add(new ReceivedBatch(result.AgentId, Guid.CreateVersion7(), new byte[32], clock.Now, clock.Now, 1, 16 * 1024 * 1024));
                await db.SaveChangesAsync(token);
            }
            using (var capacityPage = await reader.GetAsync($"/devices/{device.Id:D}", token))
            {
                var page = await new HtmlParser().ParseDocumentAsync(await capacityPage.Content.ReadAsStringAsync(token), token);
                Assert.Contains("中央新批次接收已達限制", page.Body!.TextContent);
                Assert.Contains("既有收據仍可確認重送", page.Body.TextContent);
            }
            using (var newBatch = await agent.PostAsync("/api/v1/agent/batches", JsonBytes(AgentBatchCodec.Serialize(futureBatch with { BatchId = Guid.CreateVersion7() })), token))
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, newBatch.StatusCode);
            using (var duplicateAtCapacity = await agent.PostAsync("/api/v1/agent/batches", JsonBytes(batchBytes), token))
                Assert.AreEqual(HttpStatusCode.NoContent, duplicateAtCapacity.StatusCode);
            var previousSettings = AgentSettingsSnapshot.Create(AgentSettings.Default);
            var failedReport = queueReport with { Sequence = 5, Configuration = new(previousSettings, clock.Now, AgentConfigurationFailure.Invalid) };
            using (var report = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", failedReport, token))
                Assert.AreEqual(HttpStatusCode.NoContent, report.StatusCode);
            using (var details = await reader.GetAsync($"/devices/{device.Id:D}", token))
            {
                var page = await new HtmlParser().ParseDocumentAsync(await details.Content.ReadAsStringAsync(token), token);
                Assert.Contains("同步失敗", page.Body!.TextContent);
                Assert.Contains("收到的設定無法套用，Agent 會保留上次設定", page.Body.TextContent);
            }
            using (var report = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", failedReport with { Sequence = 6, Configuration = new(previousSettings, clock.Now, null) }, token))
                Assert.AreEqual(HttpStatusCode.NoContent, report.StatusCode);
            using (var details = await reader.GetAsync($"/devices/{device.Id:D}", token))
            {
                var page = await new HtmlParser().ParseDocumentAsync(await details.Content.ReadAsStringAsync(token), token);
                Assert.Contains("待同步", page.Body!.TextContent);
                Assert.Contains("期望心跳間隔", page.Body.TextContent);
            }
            clock.Now += TimeSpan.FromMinutes(10);
            using (var details = await reader.GetAsync($"/devices/{device.Id:D}", token))
            {
                var page = await new HtmlParser().ParseDocumentAsync(await details.Content.ReadAsStringAsync(token), token);
                Assert.Contains("Agent 已失聯", page.Body!.TextContent);
                Assert.Contains("Ping", page.Body.TextContent);
            }
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
                Assert.IsNotNull((await db.Agents.SingleAsync(x => x.Id == result.AgentId, token)).LastHeartbeatAt);
            using (var apiAccess = await agent.GetAsync("/api/v1/access", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, apiAccess.StatusCode);
            using (var agentCannotAudit = await agent.GetAsync("/api/v1/operations/audit?from=2026-11-01T00:00:00Z&to=2026-11-02T00:00:00Z", token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, agentCannotAudit.StatusCode);
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

    private sealed class UnknownLengthJsonContent(int length) : HttpContent
    {
        protected override bool TryComputeLength(out long value) { value = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(new byte[length]).AsTask();
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

    private sealed class CapturedLogs : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(Messages);
        public void Dispose() { }

        private sealed class CaptureLogger(System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level != LogLevel.None;
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                messages.Enqueue(formatter(state, exception));
                if (exception is not null) messages.Enqueue(exception.ToString());
            }
        }
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
