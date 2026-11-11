using System.Net;
using System.Net.Http.Json;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Contracts.Services;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;
using Vigiloom.Infrastructure.LogAdmission.Persistence;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.Resources.Domain;
using Microsoft.AspNetCore.WebUtilities;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AgentSettingsUiHttpTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task HostLogSources_AuthorizeMutationsAndIsolateAgentSettings() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
            var first = new Device("first log host", "127.0.0.1", true, DateTimeOffset.UtcNow);
            var second = new Device("second log host", "127.0.0.2", true, DateTimeOffset.UtcNow);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
            { db.Devices.AddRange(first, second); await db.SaveChangesAsync(token); }
            async Task<string> EnrollAsync(Guid device)
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var identity = scope.ServiceProvider.GetRequiredService<AgentIdentity>();
                var issue = await identity.IssueAsync(device, Guid.CreateVersion7(), token);
                var secret = RandomNumberGenerator.GetBytes(32);
                var registration = await identity.RedeemAsync(issue.Secret, SHA256.HashData(secret), token);
                return $"vla_{registration.AgentId:D}.{WebEncoders.Base64UrlEncode(secret)}";
            }
            var firstToken = await EnrollAsync(first.Id);
            var secondToken = await EnrollAsync(second.Id);
            var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };
            using var anonymous = factory.CreateClient(options);
            using var reader = factory.CreateClient(options);
            using var admin = factory.CreateClient(options);
            using var agent = factory.CreateClient(options);
            await LoginAsync(reader, "reader", token);
            await LoginAsync(admin, "admin", token);
            var url = $"/api/v1/agent-management/devices/{first.Id:D}/log-sources/";
            using (var denied = await anonymous.GetAsync(url, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            using (var denied = await reader.GetAsync(url, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            var input = new HostLogSourceCreate(AgentLogSourceKind.Journal, "system", "local", true, null);
            using (var anti = await reader.GetAsync("/api/v1/session/antiforgery", token))
            {
                var json = await anti.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: token);
                reader.DefaultRequestHeaders.Add("RequestVerificationToken", json.GetProperty("requestToken").GetString());
            }
            using (var denied = await reader.PostAsJsonAsync(url, input, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using (var denied = await admin.PostAsJsonAsync(url, input, token)) Assert.AreEqual(HttpStatusCode.BadRequest, denied.StatusCode);
            using (var anti = await admin.GetAsync("/api/v1/session/antiforgery", token))
            {
                var json = await anti.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: token);
                admin.DefaultRequestHeaders.Add("RequestVerificationToken", json.GetProperty("requestToken").GetString());
            }
            using (var missing = await admin.PostAsJsonAsync(url, new { }, token)) Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
            using var created = await admin.PostAsJsonAsync(url, input, token);
            Assert.AreEqual(HttpStatusCode.Created, created.StatusCode);
            Assert.IsNotNull(created.Headers.Location);
            var source = await created.Content.ReadFromJsonAsync<HostLogSourceView>(cancellationToken: token);
            Assert.IsNotNull(source);
            using (var detail = await admin.GetAsync(created.Headers.Location, token)) Assert.AreEqual(HttpStatusCode.OK, detail.StatusCode);
            agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", firstToken);
            using (var denied = await agent.GetAsync(url, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            using var response = await agent.GetAsync("/api/v1/agent/log-settings", token);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.CacheControl!.NoStore);
            var snapshot = await response.Content.ReadFromJsonAsync<AgentLogSettingsSnapshot>(cancellationToken: token);
            Assert.IsNotNull(snapshot);
            Assert.IsTrue(snapshot.IsValid());
            Assert.AreEqual(100, snapshot.MaxBatchRecords);
            Assert.AreEqual(source.Settings.SourceId, snapshot.Sources.Single().SourceId);
            var epoch = Guid.CreateVersion7();
            using (var session = await agent.PostAsJsonAsync("/api/v1/agent/queue-session", new AgentQueueSessionRequest(null, epoch), token))
                Assert.AreEqual(HttpStatusCode.NoContent, session.StatusCode);
            var observation = new AgentLogSourceObservation(source.Settings.SourceId, source.Settings.Generation,
                AgentLogSourceState.Unavailable, DateTimeOffset.UtcNow, 0);
            var heartbeat = new AgentHeartbeatReport(1, epoch, 1, null, LogStatus: new(snapshot.Revision, false, [observation]));
            using (var sent = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", heartbeat, token)) Assert.AreEqual(HttpStatusCode.NoContent, sent.StatusCode);
            using (var statusPage = await admin.GetAsync($"/devices/{first.Id:D}/log-sources", token))
                Assert.Contains("無法讀取來源", await BodyTextAsync(statusPage, token));
            var duplicateSource = heartbeat with { Sequence = 2, LogStatus = heartbeat.LogStatus! with { Sources = [observation, observation] } };
            using (var invalid = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", duplicateSource, token)) Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            var wrongGeneration = heartbeat with { Sequence = 2, LogStatus = heartbeat.LogStatus! with { Sources = [observation with { Generation = Guid.CreateVersion7() }] } };
            using (var sent = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", wrongGeneration, token)) Assert.AreEqual(HttpStatusCode.NoContent, sent.StatusCode);
            using (var stale = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", heartbeat, token)) Assert.AreEqual(HttpStatusCode.NoContent, stale.StatusCode);
            using (var statusPage = await admin.GetAsync($"/devices/{first.Id:D}/log-sources", token))
                Assert.Contains("尚未檢查", await BodyTextAsync(statusPage, token));
            var replacementEpoch = Guid.CreateVersion7();
            using (var replacement = await agent.PostAsJsonAsync("/api/v1/agent/queue-session", new AgentQueueSessionRequest(epoch, replacementEpoch), token))
                Assert.AreEqual(HttpStatusCode.NoContent, replacement.StatusCode);
            using (var oldSession = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", heartbeat with { Sequence = 5 }, token))
                Assert.AreEqual(HttpStatusCode.Conflict, oldSession.StatusCode);
            using (var statusPage = await admin.GetAsync($"/devices/{first.Id:D}/log-sources", token))
                Assert.Contains("尚無目前設定的狀態", await BodyTextAsync(statusPage, token));
            using (var sent = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", heartbeat with { Epoch = replacementEpoch, Sequence = 1 }, token)) Assert.AreEqual(HttpStatusCode.NoContent, sent.StatusCode);
            var largeStatus = new AgentLogStatusReport(snapshot.Revision, true, Enumerable.Range(0, AgentLogSettingsSnapshot.MaximumSources)
                .Select(_ => new AgentLogSourceObservation(Guid.CreateVersion7(), Guid.CreateVersion7(), AgentLogSourceState.Unavailable, DateTimeOffset.UtcNow, 2_592_000)).ToArray());
            Assert.IsLessThan(8192, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(heartbeat with { LogStatus = largeStatus }).Length);
            using (var sent = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", heartbeat with { Epoch = replacementEpoch, Sequence = 2, LogStatus = largeStatus }, token))
                Assert.AreEqual(HttpStatusCode.NoContent, sent.StatusCode);
            using (var statusPage = await admin.GetAsync($"/devices/{first.Id:D}/log-sources", token))
                Assert.Contains("尚未檢查", await BodyTextAsync(statusPage, token));
            using (var older = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", heartbeat with { Epoch = replacementEpoch, Sequence = 3, LogStatus = null }, token))
                Assert.AreEqual(HttpStatusCode.NoContent, older.StatusCode);
            using (var statusPage = await admin.GetAsync($"/devices/{first.Id:D}/log-sources", token))
                Assert.Contains("尚無目前設定的狀態", await BodyTextAsync(statusPage, token));
            using (var sent = await agent.PostAsJsonAsync("/api/v1/agent/heartbeat", heartbeat with { Epoch = replacementEpoch, Sequence = 4 }, token))
                Assert.AreEqual(HttpStatusCode.NoContent, sent.StatusCode);
            agent.DefaultRequestHeaders.IfNoneMatch.Add(response.Headers.ETag!);
            using (var unchanged = await agent.GetAsync("/api/v1/agent/log-settings", token)) Assert.AreEqual(HttpStatusCode.NotModified, unchanged.StatusCode);
            var quota = await admin.GetFromJsonAsync<Vigiloom.Contracts.Operations.LogQuotaSettings>("/api/v1/log-quota/", token);
            Assert.IsNotNull(quota);
            using (var limited = await admin.PutAsJsonAsync("/api/v1/log-quota/", quota with { SourceBurst = 1 }, token))
                Assert.AreEqual(HttpStatusCode.OK, limited.StatusCode);
            using (var changed = await agent.GetAsync("/api/v1/agent/log-settings", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode);
                var smaller = await changed.Content.ReadFromJsonAsync<AgentLogSettingsSnapshot>(cancellationToken: token);
                Assert.IsNotNull(smaller);
                Assert.IsTrue(smaller.IsValid());
                Assert.AreEqual(1, smaller.MaxBatchRecords);
                Assert.AreNotEqual(snapshot.Revision, smaller.Revision);
            }
            using (var statusPage = await admin.GetAsync($"/devices/{first.Id:D}/log-sources", token))
                Assert.Contains("尚無目前設定的狀態", await BodyTextAsync(statusPage, token));
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var identity = scope.ServiceProvider.GetRequiredService<AgentIdentity>();
                Assert.AreEqual(AgentIdentityStatus.Success, await identity.RevokeAsync(Guid.Parse(firstToken.AsSpan(4, 36)), Guid.CreateVersion7(), token));
            }
            using (var revoked = await agent.GetAsync("/api/v1/agent/log-settings", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);
            agent.DefaultRequestHeaders.IfNoneMatch.Clear();
            agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secondToken);
            var isolated = await agent.GetFromJsonAsync<AgentLogSettingsSnapshot>("/api/v1/agent/log-settings", token);
            Assert.IsEmpty(isolated!.Sources);
            var update = new HostLogSourceUpdate("renamed", false, null, source.Version);
            using var updated = await admin.PutAsJsonAsync(url + source.Settings.SourceId, update, token);
            Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
            var saved = await updated.Content.ReadFromJsonAsync<HostLogSourceView>(cancellationToken: token);
            Assert.IsNotNull(saved);
            using (var stale = await admin.PutAsJsonAsync(url + source.Settings.SourceId, update, token)) Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
            using (var staleRetire = await admin.PostAsJsonAsync(url + source.Settings.SourceId + "/retire", new HostLogSourceRetire(source.Version), token))
                Assert.AreEqual(HttpStatusCode.Conflict, staleRetire.StatusCode);
            using (var retired = await admin.PostAsJsonAsync(url + source.Settings.SourceId + "/retire", new HostLogSourceRetire(saved.Version), token))
                Assert.AreEqual(HttpStatusCode.OK, retired.StatusCode);
            using (var gone = await admin.GetAsync(url + source.Settings.SourceId, token)) Assert.AreEqual(HttpStatusCode.NotFound, gone.StatusCode);
            var now = DateTimeOffset.UtcNow;
            var auditUrl = $"/api/v1/operations/audit-feed/host-log-sources?from={Uri.EscapeDataString(now.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(now.AddHours(1).ToString("O"))}";
            using (var denied = await reader.GetAsync(auditUrl, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using (var denied = await anonymous.GetAsync(auditUrl, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            var audit = await admin.GetFromJsonAsync<AuditFeedPage>(auditUrl, token);
            Assert.IsNotNull(audit);
            CollectionAssert.AreEquivalent(new[] { AuditFeedAction.HostLogSourceCreated, AuditFeedAction.HostLogSourceUpdated, AuditFeedAction.HostLogSourceRetired }, audit.Items.Select(x => x.Action).ToArray());
            Assert.IsTrue(audit.Items.All(x => x.SubjectId == first.Id && x.HostLogSource?.SourceId == source.Settings.SourceId));
            using var auditPage = await admin.GetAsync("/settings/audit?source=host-log-sources", token);
            Assert.AreEqual(HttpStatusCode.OK, auditPage.StatusCode);
            Assert.Contains("新增主機日誌來源", await BodyTextAsync(auditPage, token));
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task LogQuota_SettingsRequireAdministratorAndRecordChanges() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
            var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };
            using var anonymous = factory.CreateClient(options);
            using var reader = factory.CreateClient(options);
            using var admin = factory.CreateClient(options);
            await LoginAsync(reader, "reader", token);
            await LoginAsync(admin, "admin", token);
            using (var denied = await anonymous.GetAsync("/api/v1/log-quota/", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            using (var denied = await reader.GetAsync("/api/v1/log-quota/", token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using (var denied = await reader.GetAsync("/settings/log-quota", token)) Assert.AreEqual(HttpStatusCode.Redirect, denied.StatusCode);
            var initial = await admin.GetFromJsonAsync<Vigiloom.Contracts.Operations.LogQuotaSettings>("/api/v1/log-quota/", token);
            Assert.IsNotNull(initial);
            Assert.AreEqual(20, initial.SourceRate);
            using (var denied = await admin.PutAsJsonAsync("/api/v1/log-quota/", initial, token)) Assert.AreEqual(HttpStatusCode.BadRequest, denied.StatusCode);
            using var page = await admin.GetAsync("/settings/log-quota", token);
            var fields = await FieldsAsync(page, token, "log-quota");
            fields["Form.SourceRate"] = "35";
            using var saved = await admin.PostAsync("/settings/log-quota", new FormUrlEncodedContent(fields), token);
            Assert.Contains("Log 配額設定已儲存", await BodyTextAsync(saved, token));
            var latest = await admin.GetFromJsonAsync<Vigiloom.Contracts.Operations.LogQuotaSettings>("/api/v1/log-quota/", token);
            Assert.IsNotNull(latest);
            Assert.AreEqual(35, latest.SourceRate);
            using (var anti = await admin.GetAsync("/api/v1/session/antiforgery", token))
            {
                var json = await anti.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: token);
                admin.DefaultRequestHeaders.Add("RequestVerificationToken", json.GetProperty("requestToken").GetString());
            }
            using (var stale = await admin.PutAsJsonAsync("/api/v1/log-quota/", initial with { SourceRate = 40 }, token))
                Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
            using (var invalid = await admin.PutAsJsonAsync("/api/v1/log-quota/", latest with { SourceRate = 0 }, token))
                Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            using (var updated = await admin.PutAsJsonAsync("/api/v1/log-quota/", latest with { SourceRate = 40 }, token))
                Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
            using (var staleExisting = await admin.PutAsJsonAsync("/api/v1/log-quota/", latest with { SourceRate = 45 }, token))
                Assert.AreEqual(HttpStatusCode.Conflict, staleExisting.StatusCode);
            var at = DateTimeOffset.UtcNow;
            var auditUrl = $"/api/v1/operations/audit/log-quota?from={Uri.EscapeDataString(at.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(at.AddHours(1).ToString("O"))}";
            using (var denied = await reader.GetAsync(auditUrl, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            var audit = await admin.GetFromJsonAsync<Vigiloom.Contracts.Operations.OperationAuditPage>(auditUrl, token);
            Assert.IsNotNull(audit);
            Assert.HasCount(2, audit.Items);
            Assert.AreEqual(40, audit.Items[0].LogQuota!.Current.SourceRate);
            using (var denied = await reader.GetAsync("/api/v1/trace-quota/", token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using var tracePage = await admin.GetAsync("/settings/trace-quota", token);
            var traceFields = await FieldsAsync(tracePage, token, "log-quota");
            Assert.AreEqual("200", traceFields["Form.SourceRate"]);
            traceFields["Form.SourceRate"] = "250";
            using var traceSaved = await admin.PostAsync("/settings/trace-quota", new FormUrlEncodedContent(traceFields), token);
            Assert.Contains("Trace 配額設定已儲存", await BodyTextAsync(traceSaved, token));
            var trace = await admin.GetFromJsonAsync<Vigiloom.Contracts.Operations.LogQuotaSettings>("/api/v1/trace-quota/", token);
            Assert.IsNotNull(trace);
            Assert.AreEqual(250, trace.SourceRate);
            using var traceSlashPage = await admin.GetAsync("/settings/trace-quota/", token);
            var slashFields = await FieldsAsync(traceSlashPage, token, "log-quota");
            Assert.AreEqual("250", slashFields["Form.SourceRate"]);
            slashFields["Form.SourceRate"] = "275";
            using var slashSaved = await admin.PostAsync("/settings/trace-quota/", new FormUrlEncodedContent(slashFields), token);
            Assert.Contains("Trace 配額設定已儲存", await BodyTextAsync(slashSaved, token));
            var traceAfterSlash = await admin.GetFromJsonAsync<Vigiloom.Contracts.Operations.LogQuotaSettings>("/api/v1/trace-quota/", token);
            Assert.AreEqual(275, traceAfterSlash!.SourceRate);
            var logAfterTrace = await admin.GetFromJsonAsync<Vigiloom.Contracts.Operations.LogQuotaSettings>("/api/v1/log-quota/", token);
            Assert.AreEqual(40, logAfterTrace!.SourceRate);
            var traceAuditUrl = $"/api/v1/operations/audit/trace-quota?from={Uri.EscapeDataString(at.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(at.AddHours(1).ToString("O"))}";
            var traceAudit = await admin.GetFromJsonAsync<Vigiloom.Contracts.Operations.OperationAuditPage>(traceAuditUrl, token);
            Assert.IsNotNull(traceAudit);
            Assert.HasCount(2, traceAudit.Items);
            Assert.AreEqual(275, traceAudit.Items[0].TraceQuota!.Current.SourceRate);
            using var auditPage = await admin.GetAsync("/settings/audit?source=log-quota", token);
            Assert.Contains("Log 配額", await BodyTextAsync(auditPage, token));
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task ServiceInstancePage_PolicyAndLifecycleRequireAdminAndRemainUsableAcrossPosts() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
            var contexts = factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
            var now = DateTimeOffset.UtcNow;
            var source = new Vigiloom.Modules.ServiceObservability.Domain.ServiceSource("source", new byte[32], now);
            var service = new Vigiloom.Modules.ServiceObservability.Domain.MonitoredService(
                new Vigiloom.Modules.ServiceObservability.Domain.ServiceCandidate(source.Id, "orders", "prod", "Orders", now), now);
            Guid instanceId;
            await using (var db = await contexts.CreateDbContextAsync(token))
            {
                db.Sources.Add(source); db.Services.Add(service); await db.SaveChangesAsync(token);
                await using var tx = await db.Database.BeginTransactionAsync(token);
                var result = await ServiceInstanceResolver.ResolveAsync(db.Database.GetDbConnection(), tx.GetDbTransaction(), source.Id, "orders", "prod", "node", token);
                instanceId = result.InstanceId!.Value;
                await tx.CommitAsync(token);
            }
            var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };
            using var anonymous = factory.CreateClient(options);
            using var reader = factory.CreateClient(options);
            using var admin = factory.CreateClient(options);
            await LoginAsync(reader, "reader", token); await LoginAsync(admin, "admin", token);
            var url = $"/settings/service-instances?service={service.Id:D}";
            using (var denied = await reader.GetAsync(url, token)) Assert.AreEqual(HttpStatusCode.Redirect, denied.StatusCode);
            using (var denied = await anonymous.GetAsync("/api/v1/service-instances/policy", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            using (var denied = await reader.GetAsync("/api/v1/service-instances/policy", token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using (var denied = await admin.PutAsJsonAsync("/api/v1/service-instances/policy", new InstancePolicyInput(2, 5, ""), token)) Assert.AreEqual(HttpStatusCode.BadRequest, denied.StatusCode);
            using var page = await admin.GetAsync(url, token);
            var fields = await FieldsAsync(page, token, "instance-policy");
            fields["PolicyForm.PerService"] = "2"; fields["PolicyForm.Total"] = "5";
            using var saved = await admin.PostAsync(url, new FormUrlEncodedContent(fields), token);
            Assert.Contains("容量設定已儲存", await BodyTextAsync(saved, token));
            var action = await FieldsAsync(saved, token, "instance-action");
            action["InstanceForm.Id"] = instanceId.ToString("D"); action[$"confirm-{instanceId:N}"] = "true";
            using var retired = await admin.PostAsync(url, new FormUrlEncodedContent(action), token);
            Assert.Contains("執行個體已退役", await BodyTextAsync(retired, token));
            var after = await FieldsAsync(retired, token, "instance-policy");
            Assert.AreEqual("2", after["PolicyForm.PerService"]); Assert.AreEqual("5", after["PolicyForm.Total"]);
            Assert.IsFalse(string.IsNullOrEmpty(after["PolicyForm.Version"]));
            after["PolicyForm.Total"] = "6";
            using var again = await admin.PostAsync(url, new FormUrlEncodedContent(after), token);
            Assert.Contains("容量設定已儲存", await BodyTextAsync(again, token));
            var restore = await FieldsAsync(again, token, "instance-action");
            restore["InstanceForm.Id"] = instanceId.ToString("D"); restore[$"confirm-{instanceId:N}"] = "true";
            using var restored = await admin.PostAsync(url, new FormUrlEncodedContent(restore), token);
            Assert.Contains("執行個體已恢復", await BodyTextAsync(restored, token));
            var stopService = await FieldsAsync(restored, token, "service-collection");
            stopService["ServiceForm.Id"] = service.Id.ToString("D"); stopService[$"service-confirm-{service.Id:N}"] = "true";
            using var stopped = await admin.PostAsync(url, new FormUrlEncodedContent(stopService), token);
            Assert.Contains("服務已停收", await BodyTextAsync(stopped, token));
            var resumeService = await FieldsAsync(stopped, token, "service-collection");
            resumeService["ServiceForm.Id"] = service.Id.ToString("D"); resumeService[$"service-confirm-{service.Id:N}"] = "true";
            using var resumed = await admin.PostAsync(url, new FormUrlEncodedContent(resumeService), token);
            Assert.Contains("服務已恢復授權", await BodyTextAsync(resumed, token));
            using (var anti = await admin.GetAsync("/api/v1/session/antiforgery", token))
            {
                var json = await anti.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: token);
                admin.DefaultRequestHeaders.Add("RequestVerificationToken", json.GetProperty("requestToken").GetString());
            }
            var latest = await admin.GetFromJsonAsync<InstancePolicySnapshot>("/api/v1/service-instances/policy", token);
            using (var updated = await admin.PutAsJsonAsync("/api/v1/service-instances/policy", new InstancePolicyInput(3, 6, latest!.Version), token))
                Assert.AreEqual(HttpStatusCode.OK, updated.StatusCode);
            var instanceList = await admin.GetFromJsonAsync<ServiceInstanceSummary[]>($"/api/v1/service-instances/services/{service.Id:D}", token);
            using (var change = await admin.PostAsJsonAsync($"/api/v1/service-instances/{instanceId:D}/retire", new ServiceInstanceUpdate(instanceList!.Single().Version), token))
                Assert.AreEqual(HttpStatusCode.NoContent, change.StatusCode);
            var at = DateTimeOffset.UtcNow;
            var auditUrl = $"/api/v1/operations/audit/service-instances?from={Uri.EscapeDataString(at.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(at.AddHours(1).ToString("O"))}";
            var audit = await admin.GetFromJsonAsync<Vigiloom.Contracts.Operations.OperationAuditPage>(auditUrl, token);
            Assert.HasCount(3, audit!.Items);
            var collectionAuditUrl = $"/api/v1/operations/audit/service-collections?from={Uri.EscapeDataString(at.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(at.AddHours(1).ToString("O"))}";
            var collectionAudit = await admin.GetFromJsonAsync<Vigiloom.Contracts.Operations.OperationAuditPage>(collectionAuditUrl, token);
            Assert.HasCount(2, collectionAudit!.Items);
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task ServiceObservation_RateLimitAndCapacityDoNotConsumeAdministratorQuota() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString).WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Vigiloom:ServiceDiscovery:RequestsPerMinute"] = "2"
                })));
            await PingTestSite.InitializeAsync(factory, token);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
                await db.Database.MigrateAsync(token);
            await using var scope = factory.Services.CreateAsyncScope();
            var source = await scope.ServiceProvider.GetRequiredService<SourceCredentials>().IssueAsync("source", token);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
            {
                var now = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00') AS [Value]").SingleAsync(token);
                db.Candidates.AddRange(Enumerable.Range(0, CandidateDiscovery.PerSourceLimit)
                    .Select(i => new Vigiloom.Modules.ServiceObservability.Domain.ServiceCandidate(source.SourceId, $"key-{i}", "prod", "Service", now)));
                await db.SaveChangesAsync(token);
            }
            var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };
            using var machine = factory.CreateClient(options);
            using var admin = factory.CreateClient(options);
            await LoginAsync(admin, "admin", token);
            machine.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", source.Secret);
            const string url = "/api/v1/service-discovery/observations";
            using (var existing = await machine.PostAsJsonAsync(url, new ServiceObservation("key-0", "prod", "Updated"), token))
                Assert.AreEqual(HttpStatusCode.NoContent, existing.StatusCode);
            using (var full = await machine.PostAsJsonAsync(url, new ServiceObservation("new-key", "prod", "New"), token))
            {
                Assert.AreEqual(HttpStatusCode.ServiceUnavailable, full.StatusCode);
                Assert.AreEqual(TimeSpan.FromSeconds(60), full.Headers.RetryAfter?.Delta);
            }
            using (var limited = await machine.PostAsJsonAsync(url, new ServiceObservation("key-0", "prod", "Updated"), token))
            {
                Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);
                Assert.AreEqual(TimeSpan.FromSeconds(60), limited.Headers.RetryAfter?.Delta);
            }
            using (var list = await admin.GetAsync("/api/v1/service-discovery/candidates/", token)) Assert.AreEqual(HttpStatusCode.OK, list.StatusCode);
            await using var verify = await factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token);
            Assert.AreEqual(CandidateDiscovery.PerSourceLimit, await verify.Candidates.CountAsync(token));
            Assert.IsFalse(await verify.Candidates.AnyAsync(x => x.SourceId == source.SourceId && x.ServiceKey == "new-key", token));
            Assert.AreEqual("Updated", (await verify.Candidates.SingleAsync(x => x.SourceId == source.SourceId && x.ServiceKey == "key-0", token)).DisplayName);
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task ServiceDiscoveryPage_ManagesSourcesAndApprovesCandidate() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
            var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };
            using var admin = factory.CreateClient(options);
            using var reader = factory.CreateClient(options);
            using var anonymous = factory.CreateClient(options);
            await LoginAsync(admin, "admin", token);
            await LoginAsync(reader, "reader", token);
            const string url = "/settings/service-discovery";
            using (var deniedReports = await reader.GetAsync("/settings/collector-reports", token)) Assert.AreEqual(HttpStatusCode.Redirect, deniedReports.StatusCode);
            using (var deniedReports = await anonymous.GetAsync("/settings/collector-reports", token)) Assert.AreEqual(HttpStatusCode.Redirect, deniedReports.StatusCode);
            const string templateUrl = "/api/v1/service-reception/collector-template";
            using (var deniedTemplate = await anonymous.GetAsync(templateUrl, token)) Assert.AreNotEqual(HttpStatusCode.OK, deniedTemplate.StatusCode);
            using (var deniedTemplate = await reader.GetAsync(templateUrl, token)) Assert.AreNotEqual(HttpStatusCode.OK, deniedTemplate.StatusCode);
            using (var template = await admin.GetAsync(templateUrl, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, template.StatusCode);
                Assert.AreEqual("application/yaml", template.Content.Headers.ContentType!.MediaType);
                Assert.IsTrue(template.Headers.CacheControl!.NoStore);
                var expectedPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../deploy/collector/isolated-sources.example.yaml"));
                Assert.AreEqual(await File.ReadAllTextAsync(expectedPath, token), await template.Content.ReadAsStringAsync(token));
            }
            const string applicationTemplateUrl = "/api/v1/service-reception/application-template";
            using (var deniedTemplate = await anonymous.GetAsync(applicationTemplateUrl, token)) Assert.AreNotEqual(HttpStatusCode.OK, deniedTemplate.StatusCode);
            using (var deniedTemplate = await reader.GetAsync(applicationTemplateUrl, token)) Assert.AreNotEqual(HttpStatusCode.OK, deniedTemplate.StatusCode);
            using (var template = await admin.GetAsync(applicationTemplateUrl, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, template.StatusCode);
                Assert.AreEqual("text/plain", template.Content.Headers.ContentType!.MediaType);
                Assert.IsTrue(template.Headers.CacheControl!.NoStore);
                var expectedPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../deploy/collector/application.env.example"));
                Assert.AreEqual(await File.ReadAllTextAsync(expectedPath, token), await template.Content.ReadAsStringAsync(token));
            }
            using (var denied = await reader.GetAsync(url, token)) Assert.AreEqual(HttpStatusCode.Redirect, denied.StatusCode);
            using var page = await admin.GetAsync(url, token);
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
            var fields = await FieldsAsync(page, token, "source-create");
            fields["CreateSource.Name"] = "orders collector";
            using var created = await admin.PostAsync(url, new FormUrlEncodedContent(fields), token);
            Assert.AreEqual(HttpStatusCode.OK, created.StatusCode);
            var doc = await new HtmlParser().ParseDocumentAsync(await created.Content.ReadAsStringAsync(token), token);
            Assert.Contains("serviceKey", doc.Body!.TextContent);
            Assert.Contains("並非 OpenTelemetry 接收端", doc.Body.TextContent);
            var secret = doc.QuerySelector("textarea[aria-label='來源憑證']")!.TextContent;
            await using var scope = factory.Services.CreateAsyncScope();
            var credential = scope.ServiceProvider.GetRequiredService<SourceCredentials>();
            var sourceId = (await credential.AuthenticateAsync(secret, token))!.Value;
            Assert.AreEqual(CandidateObservation.Recorded, await scope.ServiceProvider.GetRequiredService<CandidateDiscovery>().ObserveAsync(sourceId, "orders", "prod", "Orders", token));
            var other = await scope.ServiceProvider.GetRequiredService<SourceAdministration>().IssueAsync("billing collector", Guid.CreateVersion7(), token);
            Assert.AreEqual(CandidateObservation.Recorded, await scope.ServiceProvider.GetRequiredService<CandidateDiscovery>().ObserveAsync(other.SourceId, "billing", "prod", "Billing", token));
            using var candidates = await admin.GetAsync(url, token);
            var text = await candidates.Content.ReadAsStringAsync(token);
            Assert.Contains("orders collector", text);
            Assert.Contains("Orders", text);
            Assert.Contains(sourceId.ToString("D"), text);
            Assert.Contains(other.SourceId.ToString("D"), text);
            Assert.DoesNotContain(secret, text);
            var candidate = (await scope.ServiceProvider.GetRequiredService<CandidateDiscovery>().ListAsync(1, token)).Single(x => x.SourceId == sourceId);
            var otherCandidate = (await scope.ServiceProvider.GetRequiredService<CandidateDiscovery>().ListAsync(1, token)).Single(x => x.SourceId == other.SourceId);
            var approve = await FieldsAsync(candidates, token, "service-approve");
            foreach (var key in approve.Keys.Where(x => x.StartsWith("approve-", StringComparison.Ordinal)).ToArray()) approve.Remove(key);
            approve[$"approve-{candidate.Id:N}"] = "true";
            approve["ConfirmApproval.Id"] = otherCandidate.Id.ToString("D");
            using (var mismatched = await admin.PostAsync(url, new FormUrlEncodedContent(approve), token))
                Assert.Contains("請確認服務來源及環境", await BodyTextAsync(mismatched, token));
            Assert.HasCount(2, await scope.ServiceProvider.GetRequiredService<CandidateDiscovery>().ListAsync(1, token));
            approve["ConfirmApproval.Id"] = candidate.Id.ToString("D");
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE [service].[Candidates] SET [LastSeen] = DATEADD(minute, -4, SYSUTCDATETIME()) WHERE [Id] = {candidate.Id}", token);
            using (var expired = await admin.PostAsync(url, new FormUrlEncodedContent(approve), token))
                Assert.Contains("服務已過期或無法加入", await BodyTextAsync(expired, token));
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
                Assert.AreEqual(0, await db.Services.CountAsync(token));
            Assert.AreEqual(CandidateObservation.Recorded, await scope.ServiceProvider.GetRequiredService<CandidateDiscovery>().ObserveAsync(sourceId, "orders", "prod", "Orders", token));
            using var refreshed = await admin.GetAsync(url, token);
            approve = await FieldsAsync(refreshed, token, "service-approve");
            approve[$"approve-{candidate.Id:N}"] = "true";
            approve["ConfirmApproval.Id"] = candidate.Id.ToString("D");
            using var approved = await admin.PostAsync(url, new FormUrlEncodedContent(approve), token);
            var approvedText = await BodyTextAsync(approved, token);
            Assert.Contains("服務已加入", approvedText);
            Guid monitoredServiceId;
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
            {
                var service = await db.Services.SingleAsync(x => x.SourceId == sourceId, token);
                monitoredServiceId = service.Id;
                Assert.Contains(service.Id.ToString("D"), approvedText);
            }
            var observationUrl = $"/api/v1/service-reception/collector-observations/{monitoredServiceId:D}";
            using (var reportPage = await admin.GetAsync($"/settings/collector-reports?service={monitoredServiceId:D}", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, reportPage.StatusCode);
                Assert.Contains("此頁沒有可辨識的執行個體", await BodyTextAsync(reportPage, token));
            }
            using (var deniedObservation = await anonymous.GetAsync(observationUrl, token)) Assert.AreNotEqual(HttpStatusCode.OK, deniedObservation.StatusCode);
            using (var deniedObservation = await reader.GetAsync(observationUrl, token)) Assert.AreNotEqual(HttpStatusCode.OK, deniedObservation.StatusCode);
            using (var observation = await admin.GetAsync(observationUrl, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, observation.StatusCode);
                var result = await observation.Content.ReadFromJsonAsync<CollectorObservationPage>(cancellationToken: token);
                Assert.IsNotNull(result);
                Assert.AreEqual(sourceId, result.SourceId);
                Assert.IsEmpty(result.Instances);
            }
            using (var invalidPage = await admin.GetAsync(observationUrl + "?page=2001", token)) Assert.AreEqual(HttpStatusCode.BadRequest, invalidPage.StatusCode);
            var collectorAdmin = scope.ServiceProvider.GetRequiredService<CollectorAdministration>();
            var registration = await collectorAdmin.RegisterAsync("test collector", monitoredServiceId, true, Guid.CreateVersion7(), token);
            Assert.AreEqual(CollectorChangeStatus.Updated, registration.Status);
            var mappingsUrl = $"/settings/collectors/{registration.CollectorId:D}/sources";
            using (var deniedMappings = await reader.GetAsync(mappingsUrl, token)) Assert.AreEqual(HttpStatusCode.Redirect, deniedMappings.StatusCode);
            using (var deniedMappings = await anonymous.GetAsync(mappingsUrl, token)) Assert.AreEqual(HttpStatusCode.Redirect, deniedMappings.StatusCode);
            var countersUrl = $"/settings/collectors/{registration.CollectorId:D}/instances/{Guid.CreateVersion7():D}/counters";
            using (var deniedCounters = await reader.GetAsync(countersUrl, token)) Assert.AreEqual(HttpStatusCode.Redirect, deniedCounters.StatusCode);
            using (var deniedCounters = await anonymous.GetAsync(countersUrl, token)) Assert.AreEqual(HttpStatusCode.Redirect, deniedCounters.StatusCode);
            using (var counters = await admin.GetAsync(countersUrl, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, counters.StatusCode);
                Assert.Contains("尚未設定來源對應", await BodyTextAsync(counters, token));
            }
            using (var noToken = await admin.PostAsync(mappingsUrl, new FormUrlEncodedContent(new Dictionary<string, string> { ["_handler"] = "collector-route-add" }), token))
                Assert.AreEqual(HttpStatusCode.BadRequest, noToken.StatusCode);
            using var mappingPage = await admin.GetAsync(mappingsUrl, token);
            var addMapping = await HiddenFieldsAsync(mappingPage, token, "collector-route-add");
            addMapping["AddForm.SourceId"] = other.SourceId.ToString("D");
            addMapping["AddForm.Exporter"] = "otlp_http/source-b";
            using (var readerPage = await reader.GetAsync("/account/change-password", token))
            {
                var readerDocument = await new HtmlParser().ParseDocumentAsync(await readerPage.Content.ReadAsStringAsync(token), token);
                var readerToken = readerDocument.QuerySelector("input[name='__RequestVerificationToken']")!.GetAttribute("value")!;
                var readerMutation = new Dictionary<string, string>(addMapping) { ["__RequestVerificationToken"] = readerToken };
                using var forbiddenMutation = await reader.PostAsync(mappingsUrl, new FormUrlEncodedContent(readerMutation), token);
                Assert.AreEqual(HttpStatusCode.Redirect, forbiddenMutation.StatusCode);
            }
            using var mapped = await admin.PostAsync(mappingsUrl, new FormUrlEncodedContent(addMapping), token);
            Assert.Contains("來源對應已加入", await BodyTextAsync(mapped, token));
            using (var staleMapping = await admin.PostAsync(mappingsUrl, new FormUrlEncodedContent(addMapping), token))
                Assert.Contains("資料已變更", await BodyTextAsync(staleMapping, token));
            var removeMapping = await HiddenFieldsAsync(mapped, token, "collector-route-remove");
            removeMapping["RemoveForm.SourceId"] = other.SourceId.ToString("D");
            using (var missingConfirmation = await admin.PostAsync(mappingsUrl, new FormUrlEncodedContent(removeMapping), token))
                Assert.Contains("請確認移除來源對應的影響", await BodyTextAsync(missingConfirmation, token));
            removeMapping[$"remove-{Guid.CreateVersion7():N}"] = "true";
            using (var wrongConfirmation = await admin.PostAsync(mappingsUrl, new FormUrlEncodedContent(removeMapping), token))
                Assert.Contains("請確認移除來源對應的影響", await BodyTextAsync(wrongConfirmation, token));
            removeMapping[$"remove-{other.SourceId:N}"] = "true";
            using (var removedMapping = await admin.PostAsync(mappingsUrl, new FormUrlEncodedContent(removeMapping), token))
                Assert.Contains("來源對應已移除", await BodyTextAsync(removedMapping, token));
            using (var repeatedRemoval = await admin.PostAsync(mappingsUrl, new FormUrlEncodedContent(removeMapping), token))
                Assert.Contains("資料已變更", await BodyTextAsync(repeatedRemoval, token));
            Assert.IsEmpty(await collectorAdmin.RoutesAsync(registration.CollectorId!.Value, token));
            using (var repeated = await admin.PostAsync(url, new FormUrlEncodedContent(approve), token))
                Assert.Contains("服務已加入", await BodyTextAsync(repeated, token));
            var remaining = await scope.ServiceProvider.GetRequiredService<CandidateDiscovery>().ListAsync(1, token);
            Assert.AreEqual(other.SourceId, remaining.Single().SourceId);
            using var sourcePage = await admin.GetAsync(url, token);
            var rotate = await FieldsAsync(sourcePage, token, "source-rotate");
            var sourceSummary = (await scope.ServiceProvider.GetRequiredService<SourceAdministration>().ListAsync(1, token)).Single(x => x.Id == sourceId);
            rotate["RotateSource.Selection"] = $"{sourceId:D}:{sourceSummary.Version}";
            rotate["RotateSource.Confirm"] = "true";
            using var rotated = await admin.PostAsync(url, new FormUrlEncodedContent(rotate), token);
            var rotatedDoc = await new HtmlParser().ParseDocumentAsync(await rotated.Content.ReadAsStringAsync(token), token);
            Assert.Contains("來源憑證已更換", rotatedDoc.Body!.TextContent);
            var replacementSecret = rotatedDoc.QuerySelector("textarea[aria-label='來源憑證']")!.TextContent;
            Assert.IsNull(await credential.AuthenticateAsync(secret, token));
            Assert.AreEqual(sourceId, await credential.AuthenticateAsync(replacementSecret, token));
            using (var conflict = await admin.PostAsync(url, new FormUrlEncodedContent(rotate), token))
                Assert.Contains("來源已變更", await BodyTextAsync(conflict, token));
            var revoke = await FieldsAsync(sourcePage, token, "source-revoke");
            revoke[$"revoke-{sourceId:N}"] = "true";
            revoke["ConfirmRevocation.Id"] = sourceId.ToString("D");
            using var revoked = await admin.PostAsync(url, new FormUrlEncodedContent(revoke), token);
            Assert.Contains("來源已撤銷", await BodyTextAsync(revoked, token));
            using (var repeated = await admin.PostAsync(url, new FormUrlEncodedContent(revoke), token))
                Assert.Contains("來源已撤銷", await BodyTextAsync(repeated, token));
            Assert.IsNull(await credential.AuthenticateAsync(secret, token));
            Assert.IsNull(await credential.AuthenticateAsync(replacementSecret, token));
            Assert.IsTrue(await scope.ServiceProvider.GetRequiredService<SourceAdministration>().RevokeAsync(other.SourceId, Guid.CreateVersion7(), token));
            using (var revokedBeforeRotation = await admin.PostAsync(url, new FormUrlEncodedContent(rotate), token))
                Assert.Contains("來源已變更", await BodyTextAsync(revokedBeforeRotation, token));
            using (var audit = await admin.GetAsync("/settings/audit?source=service-approvals", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, audit.StatusCode);
                var auditText = await BodyTextAsync(audit, token);
                Assert.Contains("加入服務", auditText);
                Assert.Contains(sourceId.ToString("D"), auditText);
                Assert.DoesNotContain(secret, auditText);
            }
            using (var audit = await admin.GetAsync("/settings/audit?source=service-sources", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, audit.StatusCode);
                var auditText = await BodyTextAsync(audit, token);
                Assert.Contains("建立來源", auditText);
                Assert.Contains("撤銷來源", auditText);
                Assert.Contains(sourceId.ToString("D"), auditText);
                Assert.DoesNotContain(secret, auditText);
            }
            using (var denied = await reader.GetAsync("/settings/audit?source=service-sources", token)) Assert.AreEqual(HttpStatusCode.Redirect, denied.StatusCode);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<Vigiloom.Modules.AlertingOperations.Persistence.OperationsDbContext>>().CreateDbContextAsync(token))
                await db.Database.ExecuteSqlRawAsync("UPDATE [operations].[AuditEvents] SET [Details] = N'{{}}' WHERE [Kind] = 5", token);
            using (var damaged = await admin.GetAsync("/settings/audit?source=service-sources", token))
            {
                var auditText = await BodyTextAsync(damaged, token);
                Assert.Contains("未支援的紀錄", auditText);
                Assert.Contains("未知", auditText);
                Assert.DoesNotContain("已儲存", auditText);
            }
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task ServiceSourceManagement_RequiresAdministratorAndAuditsChanges() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
            var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };
            using var anonymous = factory.CreateClient(options);
            using var reader = factory.CreateClient(options);
            using var admin = factory.CreateClient(options);
            await LoginAsync(reader, "reader", token);
            await LoginAsync(admin, "admin", token);
            const string url = "/api/v1/service-discovery/sources/";
            using (var denied = await anonymous.GetAsync(url, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            using (var denied = await reader.GetAsync(url, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using (var denied = await reader.PostAsJsonAsync(url, new ServiceSourceRequest("source"), token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using (var denied = await admin.PostAsJsonAsync(url, new ServiceSourceRequest("source"), token)) Assert.AreEqual(HttpStatusCode.BadRequest, denied.StatusCode);
            using (var anti = await admin.GetAsync("/api/v1/session/antiforgery", token))
            {
                var json = await anti.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: token);
                admin.DefaultRequestHeaders.Add("RequestVerificationToken", json.GetProperty("requestToken").GetString());
            }
            ServiceSourceCredential issued;
            using (var response = await admin.PostAsJsonAsync(url, new ServiceSourceRequest("source"), token))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.IsTrue(response.Headers.CacheControl?.NoStore);
                issued = (await response.Content.ReadFromJsonAsync<ServiceSourceCredential>(cancellationToken: token))!;
            }
            using (var list = await admin.GetAsync(url, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, list.StatusCode);
                var text = await list.Content.ReadAsStringAsync(token);
                Assert.DoesNotContain(issued.Secret, text);
                Assert.Contains(issued.SourceId.ToString("D"), text);
                Assert.IsTrue(list.Headers.CacheControl?.NoStore);
            }
            using (var response = await admin.PostAsync($"{url}{issued.SourceId:D}/revoke", null, token)) Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
            var now = DateTimeOffset.UtcNow;
            var auditUrl = $"/api/v1/operations/audit/service-sources?from={Uri.EscapeDataString(now.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(now.AddHours(1).ToString("O"))}";
            using var audit = await admin.GetAsync(auditUrl, token);
            var history = await audit.Content.ReadFromJsonAsync<Vigiloom.Contracts.Operations.OperationAuditPage>(cancellationToken: token);
            Assert.HasCount(2, history!.Items);
            Assert.IsTrue(history.Items.All(x => x.ResourceId == issued.SourceId));
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task ServiceObservation_AuthenticatesSourceAndAcceptsOnlyBoundedMetadata() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            await PingTestSite.InitializeAsync(factory, token);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
                await db.Database.MigrateAsync(token);
            await using var scope = factory.Services.CreateAsyncScope();
            var credentials = scope.ServiceProvider.GetRequiredService<SourceCredentials>();
            var first = await credentials.IssueAsync("first", token);
            var second = await credentials.IssueAsync("second", token);
            var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };
            using var client = factory.CreateClient(options);
            const string url = "/api/v1/service-discovery/observations";
            const string body = "{\"serviceKey\":\"orders\",\"environment\":\"production\",\"displayName\":\"Orders\"}";
            using (var denied = await client.PostAsJsonAsync(url, new { serviceKey = "orders" }, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", first.Secret);
            using (var accepted = await client.PostAsync(url, new StringContent(body, System.Text.Encoding.UTF8, "application/json"), token)) Assert.AreEqual(HttpStatusCode.NoContent, accepted.StatusCode);
            using (var duplicate = await client.PostAsync(url, new StringContent(body, System.Text.Encoding.UTF8, "application/json"), token)) Assert.AreEqual(HttpStatusCode.NoContent, duplicate.StatusCode);
            using (var extra = await client.PostAsync(url, new StringContent(body[..^1] + ",\"logs\":[]}", System.Text.Encoding.UTF8, "application/json"), token)) Assert.AreEqual(HttpStatusCode.BadRequest, extra.StatusCode);
            using (var duplicateProperty = await client.PostAsync(url, new StringContent(body[..^1] + ",\"serviceKey\":\"other\"}", System.Text.Encoding.UTF8, "application/json"), token)) Assert.AreEqual(HttpStatusCode.BadRequest, duplicateProperty.StatusCode);
            using (var missingProperty = await client.PostAsync(url, new StringContent("{\"serviceKey\":\"orders\"}", System.Text.Encoding.UTF8, "application/json"), token)) Assert.AreEqual(HttpStatusCode.BadRequest, missingProperty.StatusCode);
            using (var oversized = await client.PostAsync(url, new StringContent(new string('x', 2049), System.Text.Encoding.UTF8, "application/json"), token)) Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
            using (var chunked = new HttpRequestMessage(HttpMethod.Post, url))
            {
                chunked.Content = new StreamContent(new NonSeekableStream(new byte[2049]));
                chunked.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                Assert.IsNull(chunked.Content.Headers.ContentLength);
                chunked.Headers.TransferEncodingChunked = true;
                using var oversized = await client.SendAsync(chunked, token);
                Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);
            }
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", second.Secret);
            using (var accepted = await client.PostAsync(url, new StringContent(body, System.Text.Encoding.UTF8, "application/json"), token)) Assert.AreEqual(HttpStatusCode.NoContent, accepted.StatusCode);
            var rows = await scope.ServiceProvider.GetRequiredService<CandidateDiscovery>().ListAsync(1, token);
            Assert.HasCount(2, rows);
            CollectionAssert.AreEquivalent(new[] { first.SourceId, second.SourceId }, rows.Select(row => row.SourceId).ToArray());
            Assert.IsTrue(await credentials.RevokeAsync(second.SourceId, token));
            using (var revoked = await client.PostAsync(url, new StringContent(body, System.Text.Encoding.UTF8, "application/json"), token)) Assert.AreEqual(HttpStatusCode.Unauthorized, revoked.StatusCode);
            using var insecure = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("http://localhost") });
            insecure.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", first.Secret);
            using var deniedHttp = await insecure.PostAsync(url, new StringContent(body, System.Text.Encoding.UTF8, "application/json"), token);
            Assert.AreEqual(HttpStatusCode.Unauthorized, deniedHttp.StatusCode);
        }, TestContext.CancellationToken);

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }

    [TestMethod]
    public Task CandidateApproval_RequiresAdministratorAndAntiforgery() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
            await using var scope = factory.Services.CreateAsyncScope();
            var source = await scope.ServiceProvider.GetRequiredService<SourceCredentials>().IssueAsync("source", token);
            var discovery = scope.ServiceProvider.GetRequiredService<CandidateDiscovery>();
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(source.SourceId, "orders", "prod", "Orders", token));
            var candidate = (await discovery.ListAsync(1, token)).Single();
            var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };
            using var anonymous = factory.CreateClient(options);
            using var admin = factory.CreateClient(options);
            using var reader = factory.CreateClient(options);
            await LoginAsync(admin, "admin", token);
            await LoginAsync(reader, "reader", token);
            const string list = "/api/v1/service-discovery/candidates/";
            using (var denied = await anonymous.GetAsync(list, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            using (var denied = await reader.GetAsync(list, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using (var allowed = await admin.GetAsync(list, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
                Assert.IsTrue(allowed.Headers.CacheControl?.NoStore);
                var rows = await allowed.Content.ReadFromJsonAsync<CandidateSummary[]>(cancellationToken: token);
                Assert.AreEqual(candidate.Id, rows!.Single().Id);
            }
            var approve = $"{list}{candidate.Id:D}/approve";
            using (var denied = await reader.PostAsync(approve, null, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using (var denied = await admin.PostAsync(approve, null, token)) Assert.AreEqual(HttpStatusCode.BadRequest, denied.StatusCode);
            using (var anti = await admin.GetAsync("/api/v1/session/antiforgery", token))
            {
                var json = await anti.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: token);
                admin.DefaultRequestHeaders.Add("RequestVerificationToken", json.GetProperty("requestToken").GetString());
            }
            Guid serviceId;
            using (var allowed = await admin.PostAsync(approve, null, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
                serviceId = (await allowed.Content.ReadFromJsonAsync<ServiceApprovalResponse>(cancellationToken: token))!.ServiceId;
            }
            using (var repeated = await admin.PostAsync(approve, null, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, repeated.StatusCode);
                Assert.AreEqual(serviceId, (await repeated.Content.ReadFromJsonAsync<ServiceApprovalResponse>(cancellationToken: token))!.ServiceId);
            }
            Assert.IsEmpty(await discovery.ListAsync(1, token));
            var now = DateTimeOffset.UtcNow;
            var auditUrl = $"/api/v1/operations/audit/service-approvals?from={Uri.EscapeDataString(now.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(now.AddHours(1).ToString("O"))}";
            using var audit = await admin.GetAsync(auditUrl, token);
            var history = await audit.Content.ReadFromJsonAsync<Vigiloom.Contracts.Operations.OperationAuditPage>(cancellationToken: token);
            Assert.AreEqual(source.SourceId, history!.Items.Single().ServiceApproval!.SourceId);
        }, TestContext.CancellationToken);

    [TestMethod]
    public Task AdministratorCanEditAndResetOverridesWhileReaderSeesStatusOnly() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            var device = await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
            {
                var current = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
                current.Update(current.Name, current.Address, true, true, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(token);
            }
            var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };
            using var admin = factory.CreateClient(options);
            using var reader = factory.CreateClient(options);
            await LoginAsync(admin, "admin", token);
            await LoginAsync(reader, "reader", token);
            using (var denied = await reader.GetAsync("/settings/agents", token))
            {
                Assert.AreEqual(HttpStatusCode.Redirect, denied.StatusCode);
                Assert.Contains("/account/access-denied", denied.Headers.Location?.OriginalString ?? "");
            }
            using (var denied = await reader.GetAsync($"/devices/{device.Id:D}/agent-settings", token))
            {
                Assert.AreEqual(HttpStatusCode.Redirect, denied.StatusCode);
                Assert.Contains("/account/access-denied", denied.Headers.Location?.OriginalString ?? "");
            }
            using (var details = await reader.GetAsync($"/devices/{device.Id:D}", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, details.StatusCode);
                var html = await details.Content.ReadAsStringAsync(token);
                var text = (await new HtmlParser().ParseDocumentAsync(html, token)).Body!.TextContent;
                Assert.Contains("Agent 設定同步", text);
                Assert.Contains("尚未註冊 Agent", text);
                Assert.DoesNotContain($"/devices/{device.Id:D}/agent-settings", html);
            }
            using (var scope = factory.Services.CreateScope())
            {
                var metrics = scope.ServiceProvider.GetRequiredService<Vigiloom.Modules.AgentManagement.Application.AgentMetricConfiguration>();
                var current = await metrics.GetAsync(null, token);
                Assert.IsNotNull(current);
                Assert.IsNotNull(await metrics.UpdateAsync(null, new(new(45, 90, 2500, 768, 2500, 96), current.Version), Guid.CreateVersion7(), token));
            }
            using (var metricAudit = await admin.GetAsync("/settings/audit?source=agent-metric-settings", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, metricAudit.StatusCode);
                var text = await BodyTextAsync(metricAudit, token);
                Assert.Contains("變更 Agent 指標設定", text);
                Assert.Contains("有效主機採樣：45 秒", text);
                Assert.Contains("有效程序採樣：90 秒", text);
                Assert.Contains("有效採樣 CPU 預算：2500 毫秒／分鐘", text);
                Assert.Contains("有效 Agent 記憶體門檻：768 MiB", text);
                Assert.Contains("有效程序採集時間預算：2500 毫秒", text);
            }
            using (var deniedMetricAudit = await reader.GetAsync("/settings/audit?source=agent-metric-settings", token))
                Assert.AreEqual(HttpStatusCode.Redirect, deniedMetricAudit.StatusCode);
            var platformUrl = "/settings/agents";
            using (var page = await admin.GetAsync(platformUrl, token))
            {
                var inventoryPlatformFields = await FieldsAsync(page, token, "agent-inventory");
                inventoryPlatformFields["InventoryInput.ScanIntervalSeconds"] = "3600";
                using var saved = await admin.PostAsync(platformUrl, new FormUrlEncodedContent(inventoryPlatformFields), token);
                Assert.Contains("目前目標值：3600 秒", await BodyTextAsync(saved, token));
                using var stale = await admin.PostAsync(platformUrl, new FormUrlEncodedContent(inventoryPlatformFields), token);
                Assert.Contains("設定已被其他操作變更", await BodyTextAsync(stale, token));
            }
            using (var audit = await admin.GetAsync("/settings/audit?source=inventory-settings", token))
            {
                var text = await BodyTextAsync(audit, token);
                Assert.Contains("變更主機盤點設定", text);
                Assert.Contains("有效盤點間隔：3600 秒", text);
            }
            using (var denied = await reader.GetAsync("/settings/audit?source=inventory-settings", token))
                Assert.AreEqual(HttpStatusCode.Redirect, denied.StatusCode);
            using (var metricPage = await admin.GetAsync(platformUrl, token))
            {
                var metricFields = await FieldsAsync(metricPage, token, "agent-metrics");
                Assert.AreEqual("45", metricFields["MetricInput.HostIntervalSeconds"]);
                Assert.IsFalse(metricFields.ContainsKey("MetricInput.UploadLimitMiBPerMinute"));
                using var readerAntiforgery = await reader.GetAsync("/api/v1/session/antiforgery", token);
                var readerToken = await readerAntiforgery.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: token);
                var readerFields = new Dictionary<string, string>(metricFields)
                {
                    ["__RequestVerificationToken"] = readerToken.GetProperty("requestToken").GetString()!
                };
                using (var forbidden = await reader.PostAsync(platformUrl, new FormUrlEncodedContent(readerFields), token))
                    Assert.AreEqual(HttpStatusCode.Redirect, forbidden.StatusCode);
                metricFields["MetricInput.HostIntervalSeconds"] = "50";
                metricFields["MetricInput.ProcessIntervalSeconds"] = "120";
                metricFields["MetricInput.CpuMillisecondsPerMinute"] = "2000";
                metricFields["MetricInput.WorkingSetLimitMiB"] = "1024";
                metricFields["MetricInput.ProcessCollectionBudgetMs"] = "3000";
                metricFields["MetricInput.IoLimitMiBPerMinute"] = "128";
                using (var saved = await admin.PostAsync(platformUrl, new FormUrlEncodedContent(metricFields), token))
                {
                    Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
                    var text = await BodyTextAsync(saved, token);
                    Assert.Contains("目前目標值：50 秒", text);
                    Assert.Contains("目前目標值：2000 毫秒", text);
                    Assert.Contains("設定已儲存", text);
                }
                using var staleMetric = await admin.PostAsync(platformUrl, new FormUrlEncodedContent(metricFields), token);
                Assert.Contains("設定已被其他操作變更", await BodyTextAsync(staleMetric, token));
            }
            using var platform = await admin.GetAsync(platformUrl, token);
            Assert.AreEqual(HttpStatusCode.OK, platform.StatusCode);
            var platformFields = await FieldsAsync(platform, token);
            platformFields["Input.HeartbeatSeconds"] = "60";
            platformFields["Input.SettingsPollSeconds"] = "";
            platformFields["Input.BatchIntervalSeconds"] = "";
            using (var saved = await admin.PostAsync(platformUrl, new FormUrlEncodedContent(platformFields), token))
            {
                Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode);
                Assert.Contains("設定已儲存", await BodyTextAsync(saved, token));
            }
            using (var audit = await admin.GetAsync("/settings/audit?source=agent-settings", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, audit.StatusCode);
                var text = await BodyTextAsync(audit, token);
                Assert.Contains("變更 Agent 設定", text);
                Assert.Contains("有效心跳：60 秒", text);
                Assert.Contains("平台設定", text);
            }
            using (var existing = await admin.GetAsync(platformUrl, token))
            {
                var capacityFields = await FieldsAsync(existing, token, "agent-capacity");
                capacityFields["CapacityInput.PayloadMiB"] = "128";
                capacityFields["CapacityInput.BatchCount"] = "200";
                using var capacitySaved = await admin.PostAsync(platformUrl, new FormUrlEncodedContent(capacityFields), token);
                Assert.Contains("設定已儲存", await BodyTextAsync(capacitySaved, token));
                var settingsAfterCapacity = await FieldsAsync(capacitySaved, token);
                Assert.AreEqual("60", settingsAfterCapacity["Input.HeartbeatSeconds"]);
                Assert.IsFalse(string.IsNullOrEmpty(settingsAfterCapacity["Input.Version"]));
                settingsAfterCapacity["Input.HeartbeatSeconds"] = "70";
                using var settingsSaved = await admin.PostAsync(platformUrl, new FormUrlEncodedContent(settingsAfterCapacity), token);
                Assert.Contains("設定已儲存", await BodyTextAsync(settingsSaved, token));
                var capacityAfterSettings = await FieldsAsync(settingsSaved, token, "agent-capacity");
                Assert.AreEqual("128", capacityAfterSettings["CapacityInput.PayloadMiB"]);
                Assert.AreEqual("200", capacityAfterSettings["CapacityInput.BatchCount"]);
                Assert.IsFalse(string.IsNullOrEmpty(capacityAfterSettings["CapacityInput.Version"]));
                capacityAfterSettings["CapacityInput.BatchCount"] = "300";
                using var capacityResaved = await admin.PostAsync(platformUrl, new FormUrlEncodedContent(capacityAfterSettings), token);
                Assert.Contains("設定已儲存", await BodyTextAsync(capacityResaved, token));
                var settingsRestore = await FieldsAsync(capacityResaved, token);
                settingsRestore["Input.HeartbeatSeconds"] = "60";
                using var restored = await admin.PostAsync(platformUrl, new FormUrlEncodedContent(settingsRestore), token);
                Assert.Contains("設定已儲存", await BodyTextAsync(restored, token));
            }
            using (var capacityAudit = await admin.GetAsync("/settings/audit?source=agent-queue-capacity", token))
            {
                var text = await BodyTextAsync(capacityAudit, token);
                Assert.Contains("變更 Agent 佇列容量", text);
                Assert.Contains("有效容量：128 MiB", text);
                Assert.Contains("平台設定", text);
            }
            using (var stale = await admin.PostAsync(platformUrl, new FormUrlEncodedContent(platformFields), token))
                Assert.Contains("設定已被其他操作變更", await BodyTextAsync(stale, token));
            var deviceUrl = $"/devices/{device.Id:D}/agent-settings";
            using (var page = await admin.GetAsync(deviceUrl, token))
            {
                var doc = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token);
                Assert.AreEqual("沿用 3600 秒", doc.QuerySelector("#agent-inventory-interval")!.GetAttribute("placeholder"));
                Assert.Contains("Agent 尚未註冊", doc.Body!.TextContent);
                var inventoryDeviceFields = await FieldsAsync(page, token, "agent-inventory");
                inventoryDeviceFields["InventoryInput.ScanIntervalSeconds"] = "7200";
                using var saved = await admin.PostAsync(deviceUrl, new FormUrlEncodedContent(inventoryDeviceFields), token);
                Assert.Contains("目前目標值：7200 秒", await BodyTextAsync(saved, token));
                var inventoryResetFields = await FieldsAsync(saved, token, "agent-inventory");
                inventoryResetFields["InventoryInput.ScanIntervalSeconds"] = "";
                using var resetResponse = await admin.PostAsync(deviceUrl, new FormUrlEncodedContent(inventoryResetFields), token);
                Assert.Contains("目前目標值：3600 秒", await BodyTextAsync(resetResponse, token));
            }
            using (var deviceMetricPage = await admin.GetAsync(deviceUrl, token))
            {
                var doc = await new HtmlParser().ParseDocumentAsync(await deviceMetricPage.Content.ReadAsStringAsync(token), token);
                Assert.AreEqual("沿用 50 秒", doc.QuerySelector("#agent-metric-host")!.GetAttribute("placeholder"));
                Assert.AreEqual("沿用 2000 毫秒", doc.QuerySelector("#agent-metric-cpu")!.GetAttribute("placeholder"));
                Assert.AreEqual("沿用 1024 MiB", doc.QuerySelector("#agent-metric-memory")!.GetAttribute("placeholder"));
                Assert.AreEqual("沿用 3000 毫秒", doc.QuerySelector("#agent-metric-process-budget")!.GetAttribute("placeholder"));
                Assert.IsNull(doc.QuerySelector("#agent-metric-upload"));
                Assert.AreEqual("沿用 128 MiB／分鐘", doc.QuerySelector("#agent-metric-io")!.GetAttribute("placeholder"));
                var metricFields = await FieldsAsync(deviceMetricPage, token, "agent-metrics");
                metricFields["MetricInput.HostIntervalSeconds"] = "75";
                metricFields["MetricInput.CpuMillisecondsPerMinute"] = "1000";
                metricFields["MetricInput.WorkingSetLimitMiB"] = "256";
                metricFields["MetricInput.ProcessCollectionBudgetMs"] = "1000";
                metricFields["MetricInput.IoLimitMiBPerMinute"] = "32";
                using var saved = await admin.PostAsync(deviceUrl, new FormUrlEncodedContent(metricFields), token);
                Assert.Contains("目前目標值：75 秒", await BodyTextAsync(saved, token));
                Assert.Contains("目前目標值：1000 毫秒", await BodyTextAsync(saved, token));
                Assert.AreEqual("1000", (await FieldsAsync(saved, token, "agent-metrics"))["MetricInput.ProcessCollectionBudgetMs"]);
                Assert.AreEqual("32", (await FieldsAsync(saved, token, "agent-metrics"))["MetricInput.IoLimitMiBPerMinute"]);
                Assert.Contains("目前目標值：256 MiB", await BodyTextAsync(saved, token));
                Assert.Contains("目前目標值：1000 毫秒", await BodyTextAsync(saved, token));
                var resetMetric = await FieldsAsync(saved, token, "agent-metrics");
                resetMetric["MetricInput.HostIntervalSeconds"] = "";
                resetMetric["MetricInput.CpuMillisecondsPerMinute"] = "";
                resetMetric["MetricInput.WorkingSetLimitMiB"] = "";
                resetMetric["MetricInput.ProcessCollectionBudgetMs"] = "";
                resetMetric["MetricInput.IoLimitMiBPerMinute"] = "";
                using var metricResetResponse = await admin.PostAsync(deviceUrl, new FormUrlEncodedContent(resetMetric), token);
                Assert.Contains("目前目標值：50 秒", await BodyTextAsync(metricResetResponse, token));
                Assert.Contains("目前目標值：2000 毫秒", await BodyTextAsync(metricResetResponse, token));
                Assert.Contains("目前目標值：1024 MiB", await BodyTextAsync(metricResetResponse, token));
                Assert.Contains("目前目標值：3000 毫秒", await BodyTextAsync(metricResetResponse, token));
            }
            using var devicePage = await admin.GetAsync(deviceUrl, token);
            Assert.AreEqual(HttpStatusCode.OK, devicePage.StatusCode);
            var pageText = await devicePage.Content.ReadAsStringAsync(token);
            Assert.Contains("目前目標值：60 秒", (await new HtmlParser().ParseDocumentAsync(pageText, token)).Body!.TextContent);
            var fields = await FieldsAsync(devicePage, token);
            fields["Input.HeartbeatSeconds"] = "90";
            fields["Input.SettingsPollSeconds"] = "";
            fields["Input.BatchIntervalSeconds"] = "";
            using (var saved = await admin.PostAsync(deviceUrl, new FormUrlEncodedContent(fields), token))
                Assert.Contains("目前目標值：90 秒", await BodyTextAsync(saved, token));
            using var savedPage = await admin.GetAsync(deviceUrl, token);
            var savedDoc = await new HtmlParser().ParseDocumentAsync(await savedPage.Content.ReadAsStringAsync(token), token);
            Assert.AreEqual("沿用 60 秒", savedDoc.QuerySelector("#agent-heartbeat")!.GetAttribute("placeholder"));
            var reset = await FieldsAsync(savedPage, token);
            reset["Input.HeartbeatSeconds"] = "";
            reset["Input.SettingsPollSeconds"] = "";
            reset["Input.BatchIntervalSeconds"] = "";
            using (var restored = await admin.PostAsync(deviceUrl, new FormUrlEncodedContent(reset), token))
                Assert.Contains("目前目標值：60 秒", await BodyTextAsync(restored, token));
        }, TestContext.CancellationToken);

    private static async Task LoginAsync(HttpClient client, string username, CancellationToken token)
    {
        using var page = await client.GetAsync("/account/login", token);
        var document = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token);
        var form = document.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "login");
        var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
        fields["Input.UserName"] = username;
        fields["Input.Password"] = "Strong-Password-123";
        using var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(fields), token);
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<Dictionary<string, string>> FieldsAsync(HttpResponseMessage response, CancellationToken token, string formName = "agent-settings")
    {
        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token);
        var form = document.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == formName);
        return form.QuerySelectorAll("input[name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
    }

    private static async Task<Dictionary<string, string>> HiddenFieldsAsync(HttpResponseMessage response, CancellationToken token, string formName)
    {
        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token);
        var form = document.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == formName);
        return form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
    }

    private static async Task<string> BodyTextAsync(HttpResponseMessage response, CancellationToken token) =>
        (await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token)).Body!.TextContent;
}
