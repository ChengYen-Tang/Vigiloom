using System.Net;
using System.Net.Http.Json;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
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
