using System.Net;
using System.Net.Http.Json;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Contracts.Services;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class AgentSettingsUiHttpTests
{
    public TestContext TestContext { get; set; } = null!;

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
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            await PingTestSite.InitializeAsync(factory, token);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
                await db.Database.MigrateAsync(token);
            var options = new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") };
            using var admin = factory.CreateClient(options);
            using var reader = factory.CreateClient(options);
            await LoginAsync(admin, "admin", token);
            await LoginAsync(reader, "reader", token);
            const string url = "/settings/service-discovery";
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
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
            {
                var service = await db.Services.SingleAsync(x => x.SourceId == sourceId, token);
                Assert.Contains(service.Id.ToString("D"), approvedText);
            }
            using (var repeated = await admin.PostAsync(url, new FormUrlEncodedContent(approve), token))
                Assert.Contains("服務已加入", await BodyTextAsync(repeated, token));
            var remaining = await scope.ServiceProvider.GetRequiredService<CandidateDiscovery>().ListAsync(1, token);
            Assert.AreEqual(other.SourceId, remaining.Single().SourceId);
            using var sourcePage = await admin.GetAsync(url, token);
            var revoke = await FieldsAsync(sourcePage, token, "source-revoke");
            revoke[$"revoke-{sourceId:N}"] = "true";
            revoke["ConfirmRevocation.Id"] = sourceId.ToString("D");
            using var revoked = await admin.PostAsync(url, new FormUrlEncodedContent(revoke), token);
            Assert.Contains("來源已撤銷", await BodyTextAsync(revoked, token));
            using (var repeated = await admin.PostAsync(url, new FormUrlEncodedContent(revoke), token))
                Assert.Contains("來源已撤銷", await BodyTextAsync(repeated, token));
            Assert.IsNull(await credential.AuthenticateAsync(secret, token));
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
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            await PingTestSite.InitializeAsync(factory, token);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
                await db.Database.MigrateAsync(token);
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
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            await PingTestSite.InitializeAsync(factory, token);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
                await db.Database.MigrateAsync(token);
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
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            var device = await PingTestSite.InitializeAsync(factory, token);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
                await db.Database.MigrateAsync(token);
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
            var platformUrl = "/settings/agents";
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

    private static async Task<string> BodyTextAsync(HttpResponseMessage response, CancellationToken token) =>
        (await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token)).Body!.TextContent;
}
