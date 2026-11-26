using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Agent.Baseline;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class CustomBaselineHttpTests(TestContext context)
{
    [TestMethod]
    public Task DraftRequiresAdministratorAndApprovalBeforeDelivery_AndSupportsRollback() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
            var device = new Device("baseline custom host", "127.0.0.1", true, DateTimeOffset.UtcNow);
            await using (var resources = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
            { resources.Devices.Add(device); await resources.SaveChangesAsync(token); }
            string credential;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var identity = scope.ServiceProvider.GetRequiredService<AgentIdentity>();
                var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
                var secret = RandomNumberGenerator.GetBytes(32);
                var registered = await identity.RedeemAsync(issued.Secret, SHA256.HashData(secret), token);
                credential = $"vla_{registered.AgentId:D}.{WebEncoders.Base64UrlEncode(secret)}";
            }
            using var agent = PingTestSite.Client(factory);
            agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            using var anonymous = PingTestSite.Client(factory);
            using var reader = PingTestSite.Client(factory);
            await PingTestSite.LoginAsync(reader, "reader", token);
            using var admin = PingTestSite.Client(factory);
            await PingTestSite.LoginAsync(admin, "admin", token);
            using (var anti = await admin.GetAsync("/api/v1/session/antiforgery", token))
            {
                var json = await anti.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
                admin.DefaultRequestHeaders.Add("RequestVerificationToken", json.GetProperty("requestToken").GetString());
            }
            var draft = new BaselineCheck("draft", "SSH config permissions", "linux", ["ubuntu:24.04"],
                BaselineSources.LinuxShadowOperation, "/etc/ssh/sshd_config", "0600", "https://example.test/policy", "Administrator authored", "Review access before changing permissions.\nVerify the service after changing it.");
            const string create = "/api/v1/admin/intelligence/sources/baseline-checks";
            using (var denied = await anonymous.PostAsJsonAsync(create, new { Check = draft }, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            using (var denied = await reader.PostAsJsonAsync(create, new { Check = draft }, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using (var denied = await anonymous.GetAsync("/settings/baseline-checks", token)) Assert.AreNotEqual(HttpStatusCode.OK, denied.StatusCode);
            CustomBaselineStageResult saved;
            using (var response = await admin.PostAsJsonAsync(create, new { Check = draft }, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                saved = (await response.Content.ReadFromJsonAsync<CustomBaselineStageResult>(cancellationToken: token))!;
            }
            Assert.AreEqual(CustomBaselineStageStatus.Staged, saved.Status);
            var sourceId = saved.SourceId!.Value;
            var firstRevision = saved.RevisionId!.Value;
            var editUrl = $"/settings/baseline-checks?source={sourceId:D}&revision={firstRevision:D}";
            admin.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
            using (var english = await admin.GetAsync(editUrl, token))
            {
                var text = WebUtility.HtmlDecode(await english.Content.ReadAsStringAsync(token));
                Assert.Contains("Custom security baseline", text);
                Assert.Contains("SSH config permissions", text);
                Assert.Contains("/etc/ssh/sshd_config", text);
            }
            admin.DefaultRequestHeaders.AcceptLanguage.Clear();
            admin.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-Hans");
            using (var simplified = await admin.GetAsync(editUrl, token))
                Assert.Contains("自定义安全基准", WebUtility.HtmlDecode(await simplified.Content.ReadAsStringAsync(token)));
            admin.DefaultRequestHeaders.AcceptLanguage.Clear();
            await using var serviceScope = factory.Services.CreateAsyncScope();
            var stage = serviceScope.ServiceProvider.GetRequiredService<CustomBaselineStage>();
            var sources = serviceScope.ServiceProvider.GetRequiredService<IBaselineSource>();
            var page = await stage.SourcePageAsync(sourceId, token);
            using (var review = await admin.GetAsync($"/settings/intelligence?source={sourceId:D}&sourcePage={page}", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, review.StatusCode);
                var body = await review.Content.ReadAsStringAsync(token);
                Assert.Contains("SSH config permissions", body);
                Assert.Contains("/etc/ssh/sshd_config", body);
                Assert.Contains("0600", body);
                Assert.Contains("Review access before changing permissions", body);
            }
            using (var response = await agent.GetAsync("/api/v1/agent/baseline-settings", token))
            {
                var settings = await response.Content.ReadFromJsonAsync<BaselineSettingsSnapshot>(cancellationToken: token);
                Assert.IsFalse(settings!.Checks.Any(x => x.SourceId == sourceId));
            }
            using (var response = await reader.GetAsync($"/api/v1/devices/{device.Id:D}/baseline", token))
            {
                var view = await response.Content.ReadFromJsonAsync<DeviceBaselineView>(cancellationToken: token);
                Assert.IsFalse(view!.Checks.Any(x => x.SourceKey == $"{BaselineSources.LocalPrefix}{sourceId:D}"));
            }
            var activation = $"/api/v1/admin/intelligence/sources/{sourceId:D}/activation";
            using (var response = await admin.PostAsJsonAsync(activation, new IntelligenceActivationRequest(firstRevision, null, "", false), token))
                Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
            var first = await sources.GetActiveAsync($"{BaselineSources.LocalPrefix}{sourceId:D}", token);
            Assert.AreEqual(BaselineSourceAvailability.Ready, first.Availability);
            Assert.AreEqual("0600", first.Check!.Expected);
            var observedAt = DateTimeOffset.UtcNow;
            var observation = new BaselineResult(sourceId, firstRevision, first.ActivationVersion!, first.Check.Id, observedAt,
                "ubuntu:24.04", BaselineOutcome.Fail, "Mode 640; extended ACL absent");
            async Task SendObservation(BaselineResult value)
            {
                var batch = new BaselineBatch(BaselineBatchCodec.SchemaVersion, Guid.CreateVersion7(), DateTimeOffset.UtcNow, [value]);
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/agent/batches") { Content = new ByteArrayContent(BaselineBatchCodec.Serialize(batch)) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var response = await agent.SendAsync(request, token);
                Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
            }
            await SendObservation(observation);
            using (var response = await reader.GetAsync($"/api/v1/devices/{device.Id:D}/baseline", token))
            {
                var view = await response.Content.ReadFromJsonAsync<DeviceBaselineView>(cancellationToken: token);
                Assert.IsTrue(view!.Checks.Single(x => x.SourceKey == $"{BaselineSources.LocalPrefix}{sourceId:D}").Latest!.Current);
            }
            var noteUrl = $"/api/v1/admin/devices/{device.Id:D}/baseline-exceptions";
            var note = new BaselineExceptionInput($"{BaselineSources.LocalPrefix}{sourceId:D}", firstRevision, "Approved temporary operational exception");
            using (var denied = await reader.PostAsJsonAsync(noteUrl, note, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
            using (var denied = await anonymous.PostAsJsonAsync(noteUrl, note, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
            using (var response = await admin.PostAsJsonAsync(noteUrl, note, token)) Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
            using (var response = await reader.GetAsync($"/api/v1/devices/{device.Id:D}/baseline", token))
            {
                var view = await response.Content.ReadFromJsonAsync<DeviceBaselineView>(cancellationToken: token);
                var check = view!.Checks.Single(x => x.SourceKey == note.SourceKey);
                Assert.AreEqual(BaselineOutcome.Fail, check.Latest!.Outcome);
                Assert.AreEqual(note.Reason, check.Notes!.Single().Reason);
                Assert.AreEqual(firstRevision, check.Notes!.Single().RevisionId);
            }
            var audit = serviceScope.ServiceProvider.GetRequiredService<OperationAuditQueryService>();
            var auditPage = await audit.QueryIntelligenceAsync(new OperationAuditQuery(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5)), token);
            Assert.IsTrue(auditPage.Items.Any(x => x.ResourceId == device.Id && x.BaselineException?.Reason == note.Reason));
            var sourceVersion = (await stage.ReadDraftAsync(sourceId, firstRevision, token))!.SourceVersion;
            CustomBaselineStageResult changed;
            using (var response = await admin.PostAsJsonAsync($"/api/v1/admin/intelligence/sources/{sourceId:D}/baseline-revisions",
                new { Check = draft with { Expected = "0640" }, ExpectedVersion = sourceVersion }, token))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                changed = (await response.Content.ReadFromJsonAsync<CustomBaselineStageResult>(cancellationToken: token))!;
            }
            using (var response = await agent.GetAsync("/api/v1/agent/baseline-settings", token))
            {
                var settings = await response.Content.ReadFromJsonAsync<BaselineSettingsSnapshot>(cancellationToken: token);
                Assert.AreEqual(firstRevision, settings!.Checks.Single(x => x.SourceId == sourceId).RevisionId);
            }
            var secondRevision = changed.RevisionId!.Value;
            using (var response = await admin.PostAsJsonAsync(activation, new IntelligenceActivationRequest(secondRevision, firstRevision, first.ActivationVersion!, false), token))
                Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
            var second = await sources.GetActiveAsync($"{BaselineSources.LocalPrefix}{sourceId:D}", token);
            Assert.AreEqual("0640", second.Check!.Expected);
            using (var response = await reader.GetAsync($"/api/v1/devices/{device.Id:D}/baseline", token))
            {
                var view = await response.Content.ReadFromJsonAsync<DeviceBaselineView>(cancellationToken: token);
                var check = view!.Checks.Single(x => x.SourceKey == $"{BaselineSources.LocalPrefix}{sourceId:D}");
                Assert.IsFalse(check.Latest!.Current);
                Assert.AreEqual("0600", check.Latest.Definition!.Expected);
                Assert.AreEqual("0640", check.Definition!.Expected);
                Assert.AreEqual(firstRevision, check.Notes!.Single().RevisionId);
                Assert.AreEqual(secondRevision, check.ActiveRevisionId);
            }
            using (var response = await admin.PostAsJsonAsync(noteUrl, note, token)) Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
            using (var response = await admin.PostAsJsonAsync(activation, new IntelligenceActivationRequest(firstRevision, secondRevision, second.ActivationVersion!, false), token))
                Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
            var restored = await sources.GetActiveAsync($"{BaselineSources.LocalPrefix}{sourceId:D}", token);
            Assert.AreEqual(firstRevision, restored.RevisionId);
            Assert.AreNotEqual(first.ActivationVersion, restored.ActivationVersion);
            Assert.AreEqual("0600", restored.Check!.Expected);
            await using (var security = await factory.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>().CreateDbContextAsync(token))
            {
                var approvals = await security.IntelligenceActivationEvents.Where(x => x.SourceId == sourceId).ToArrayAsync(token);
                Assert.HasCount(3, approvals);
                Assert.IsTrue(approvals.All(x => !x.Scheduled && x.ActorId is not null));
            }
            using (var response = await reader.GetAsync($"/api/v1/devices/{device.Id:D}/baseline", token))
            {
                var view = await response.Content.ReadFromJsonAsync<DeviceBaselineView>(cancellationToken: token);
                Assert.IsFalse(view!.Checks.Single(x => x.SourceKey == $"{BaselineSources.LocalPrefix}{sourceId:D}").Latest!.Current);
            }
            await SendObservation(observation with { ActivationVersion = restored.ActivationVersion!, ObservedAt = DateTimeOffset.UtcNow });
            using (var response = await reader.GetAsync($"/api/v1/devices/{device.Id:D}/baseline", token))
            {
                var view = await response.Content.ReadFromJsonAsync<DeviceBaselineView>(cancellationToken: token);
                Assert.IsTrue(view!.Checks.Single(x => x.SourceKey == $"{BaselineSources.LocalPrefix}{sourceId:D}").Latest!.Current);
            }
        }, context.CancellationToken);
}
