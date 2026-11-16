using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.SecurityAnalysis.Domain.Baseline;
using Vigiloom.Modules.SecurityAnalysis.Domain.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class BaselineHttpTests(TestContext context)
{
    [TestMethod]
    public Task AgentPolicyAndBatch_RequireIdentityAndDeduplicateReceipt() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
            var device = new Device("baseline host", "127.0.0.1", true, DateTimeOffset.UtcNow);
            await using (var resources = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
            {
                resources.Devices.Add(device);
                await resources.SaveChangesAsync(token);
            }
            string credential;
            Guid agentId;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var identity = scope.ServiceProvider.GetRequiredService<AgentIdentity>();
                var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
                var secret = RandomNumberGenerator.GetBytes(32);
                var registered = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
                agentId = registered.AgentId!.Value;
                credential = $"vla_{agentId:D}.{WebEncoders.Base64UrlEncode(secret)}";
            }
            using var client = PingTestSite.Client(factory);
            using (var denied = await client.GetAsync("/api/v1/agent/baseline-settings", token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            using (var denied = await client.GetAsync($"/api/v1/devices/{device.Id:D}/baseline", token))
                Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            using (var response = await client.GetAsync("/api/v1/agent/baseline-settings", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var policy = await response.Content.ReadFromJsonAsync<BaselineSettingsSnapshot>(cancellationToken: token);
                Assert.IsNotNull(policy);
                Assert.IsTrue(policy.IsValid());
                Assert.IsEmpty(policy.Checks);
            }
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var settings = scope.ServiceProvider.GetRequiredService<InventoryConfiguration>();
                var current = await settings.GetAsync(device.Id, token);
                Assert.IsNotNull(current);
                Assert.IsNotNull(await settings.UpdateAsync(device.Id, new(new(3600), current.Version), Guid.CreateVersion7(), token));
            }
            using (var response = await client.GetAsync("/api/v1/agent/baseline-settings", token))
            {
                var policy = await response.Content.ReadFromJsonAsync<BaselineSettingsSnapshot>(cancellationToken: token);
                Assert.AreEqual(3600, policy!.ScanIntervalSeconds);
            }
            var now = DateTimeOffset.UtcNow;
            var unknown = new BaselineBatch(BaselineBatchCodec.SchemaVersion, Guid.CreateVersion7(), now,
                [new(Guid.CreateVersion7(), Guid.CreateVersion7(), "AAAAAAAAAAA=", "test-check", now, "ubuntu:24.04", BaselineOutcome.Fail, "test evidence")]);
            using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/batches") { Content = new ByteArrayContent(BaselineBatchCodec.Serialize(unknown)) })
            {
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var response = await client.SendAsync(request, token);
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            }
            const string commit = "033b1b8672802ffa6bd555c373e2c73c32d25f5d";
            const string path = "linux_os/guide/system/permissions/files/permissions_important_account_files/file_permissions_etc_shadow/rule.yml";
            var check = new BaselineCheck(BaselineSources.LinuxShadowCheck, "/etc/shadow permissions", "linux",
                ["ubuntu:22.04", "ubuntu:24.04", "debian:12"], BaselineSources.LinuxShadowOperation,
                BaselineSources.LinuxShadowTarget, "0640-or-stricter", $"https://github.com/ComplianceAsCode/content/blob/{commit}/{path}", "BSD-3-Clause");
            var artifact = JsonSerializer.SerializeToUtf8Bytes(check);
            var source = new IntelligenceSource { Key = BaselineSources.LinuxShadow, Enabled = true, LastSuccessfulCheckAt = now };
            var revision = new IntelligenceRevision
            {
                SourceId = source.Id, UpstreamVersion = commit, RetrievedAt = now, Artifact = artifact, Digest = SHA256.HashData(artifact),
                SchemaVersion = "baseline-check-v1", MappingVersion = "baseline-reviewed-v1",
                LicenseEvidence = $"ComplianceAsCode BSD-3-Clause; ComplianceAsCode/content@{commit}", RecordCount = 1, Normalized = true
            };
            await using (var security = await factory.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>().CreateDbContextAsync(token))
            {
                security.IntelligenceSources.Add(source);
                security.IntelligenceRevisions.Add(revision);
                await security.SaveChangesAsync(token);
                security.IntelligenceActivations.Add(new IntelligenceActivation { SourceId = source.Id, RevisionId = revision.Id, ActivatedAt = now });
                security.IntelligenceActivationEvents.Add(new IntelligenceActivationEvent { SourceId = source.Id, RevisionId = revision.Id, OccurredAt = now, Scheduled = true });
                await security.SaveChangesAsync(token);
            }
            BaselineSelection selected;
            BaselineSettingsSnapshot currentPolicy;
            using (var response = await client.GetAsync("/api/v1/agent/baseline-settings", token))
            {
                var policy = await response.Content.ReadFromJsonAsync<BaselineSettingsSnapshot>(cancellationToken: token);
                Assert.IsNotNull(policy);
                currentPolicy = policy;
                selected = policy.Checks.Single();
                Assert.AreEqual(revision.Id, selected.RevisionId);
            }
            var epoch = Guid.CreateVersion7();
            using (var started = await client.PostAsJsonAsync("/api/v1/agent/queue-session", new AgentQueueSessionRequest(null, epoch), token))
                Assert.AreEqual(HttpStatusCode.NoContent, started.StatusCode);
            var report = new BaselineSettingsReport(currentPolicy.Revision, now, now, null);
            using (var heartbeat = await client.PostAsJsonAsync("/api/v1/agent/heartbeat", new AgentHeartbeatReport(1, epoch, 1, null, BaselineConfiguration: report), token))
                Assert.AreEqual(HttpStatusCode.NoContent, heartbeat.StatusCode);
            var batch = new BaselineBatch(BaselineBatchCodec.SchemaVersion, Guid.CreateVersion7(), now,
                [new(selected.SourceId, selected.RevisionId, selected.ActivationVersion, selected.Check.Id,
                    now, "ubuntu:24.04", BaselineOutcome.Fail, "test evidence")]);
            var wrongCheck = batch with { BatchId = Guid.CreateVersion7(), Items = [batch.Items[0] with { CheckId = "different-check" }] };
            using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/batches") { Content = new ByteArrayContent(BaselineBatchCodec.Serialize(wrongCheck)) })
            {
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var response = await client.SendAsync(request, token);
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
            }
            var payload = BaselineBatchCodec.Serialize(batch);
            for (var i = 0; i < 2; i++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/batches") { Content = new ByteArrayContent(payload) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var response = await client.SendAsync(request, token);
                Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
            }
            await using var db = await factory.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>().CreateDbContextAsync(token);
            var row = await db.Set<BaselineObservation>().SingleAsync(token);
            Assert.AreEqual(agentId, row.AgentId);
            Assert.AreEqual(device.Id, row.DeviceId);
            Assert.AreEqual(BaselineOutcome.Fail, row.Outcome);
            Assert.AreEqual("test evidence", row.Evidence);
            await using (var resources = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
            {
                var published = await resources.Devices.SingleAsync(x => x.Id == device.Id, token);
                published.SetPublication(true, "Public host", PublicationPolicy.BaselineVersion, DateTimeOffset.UtcNow);
                await resources.SaveChangesAsync(token);
            }
            using (var anonymous = PingTestSite.Client(factory))
            {
                foreach (var publicPath in new[] { "/api/v1/public/devices", $"/api/v1/public/devices/{device.Id:D}" })
                {
                    using var response = await anonymous.GetAsync(publicPath, token);
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                    var body = await response.Content.ReadAsStringAsync(token);
                    Assert.Contains("Public host", body);
                    Assert.DoesNotContain("test evidence", body);
                    Assert.DoesNotContain(BaselineSources.LinuxShadowCheck, body);
                }
            }
            using var reader = PingTestSite.Client(factory);
            await PingTestSite.LoginAsync(reader, "reader", token);
            using (var page = await reader.GetAsync($"/devices/{device.Id:D}", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
                var text = (await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token)).Body!.TextContent;
                Assert.Contains("安全設定基準", text);
                Assert.Contains("未通過", text);
                Assert.Contains("test evidence", text);
                Assert.Contains("0640", text);
                Assert.Contains("建議", text);
            }
            using (var response = await reader.GetAsync($"/api/v1/devices/{device.Id:D}/baseline", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                var view = await response.Content.ReadFromJsonAsync<DeviceBaselineView>(cancellationToken: token);
                Assert.IsNotNull(view);
                var result = view.Checks.Single(x => x.SourceKey == BaselineSources.LinuxShadow);
                Assert.AreEqual(BaselineSyncState.Applied, view.Sync!.State);
                Assert.AreEqual(currentPolicy.Revision, view.Sync.AppliedRevision);
                Assert.AreEqual(now, view.Sync.LastSyncedAt);
                Assert.IsTrue(result.Latest!.Current);
                Assert.AreEqual(BaselineOutcome.Fail, result.Latest.Outcome);
            }
            using (var heartbeat = await client.PostAsJsonAsync("/api/v1/agent/heartbeat", new AgentHeartbeatReport(1, epoch, 2, null,
                BaselineConfiguration: report with { Failure = AgentConfigurationFailure.Unavailable }), token))
                Assert.AreEqual(HttpStatusCode.NoContent, heartbeat.StatusCode);
            using (var response = await reader.GetAsync($"/api/v1/devices/{device.Id:D}/baseline", token))
            {
                var view = await response.Content.ReadFromJsonAsync<DeviceBaselineView>(cancellationToken: token);
                Assert.AreEqual(BaselineSyncState.Failed, view!.Sync!.State);
                Assert.AreEqual(now, view.Sync.LastSyncedAt);
            }
            using (var heartbeat = await client.PostAsJsonAsync("/api/v1/agent/heartbeat", new AgentHeartbeatReport(1, epoch, 3, null,
                BaselineConfiguration: report), token))
                Assert.AreEqual(HttpStatusCode.NoContent, heartbeat.StatusCode);
            await db.IntelligenceActivations.Where(x => x.SourceId == source.Id).ExecuteDeleteAsync(token);
            var historic = batch with { BatchId = Guid.CreateVersion7() };
            using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/batches") { Content = new ByteArrayContent(BaselineBatchCodec.Serialize(historic)) })
            {
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var response = await client.SendAsync(request, token);
                Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
            }
            Assert.AreEqual(2, await db.Set<BaselineObservation>().CountAsync(token));
            using (var response = await reader.GetAsync($"/api/v1/devices/{device.Id:D}/baseline", token))
            {
                var view = await response.Content.ReadFromJsonAsync<DeviceBaselineView>(cancellationToken: token);
                var result = view!.Checks.Single(x => x.SourceKey == BaselineSources.LinuxShadow);
                Assert.AreEqual(BaselineSyncState.Pending, view.Sync!.State);
                Assert.AreEqual(BaselineSourceAvailability.NoActiveRevision, result.SourceAvailability);
                Assert.IsFalse(result.Latest!.Current);
            }
            using (var page = await reader.GetAsync($"/devices/{device.Id:D}", token))
            {
                var text = (await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token)).Body!.TextContent;
                Assert.Contains("來源尚未發布", text);
                Assert.Contains("非目前有效結果", text);
            }
        }, context.CancellationToken);
}
