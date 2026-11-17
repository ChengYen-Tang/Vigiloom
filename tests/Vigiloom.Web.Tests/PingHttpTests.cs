using Vigiloom.Web.Tests.Fixtures;
using System.Net;
using System.Net.Http.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Services;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[DoNotParallelize]
public sealed class PingHttpTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task TelemetryRetentionAuthorizationAndForm() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        using var anonymous = Client(factory);
        using (var denied = await anonymous.GetAsync("/api/v1/settings/telemetry/retention", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var reader = Client(factory); await LoginAsync(reader, "reader", token);
        using (var denied = await reader.GetAsync("/api/v1/settings/telemetry/retention", token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using var admin = Client(factory); await LoginAsync(admin, "admin", token);
        var initial = await admin.GetFromJsonAsync<TelemetryRetentionSnapshot>("/api/v1/settings/telemetry/retention", token);
        Assert.IsNotNull(initial);
        using (var noCsrf = await admin.PostAsJsonAsync("/api/v1/settings/telemetry/retention/preview", new TelemetryRetentionPreviewRequest(10, 5, initial.Version), token))
            Assert.AreEqual(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using var page = await admin.GetAsync("/settings/service-reception/retention", token);
        var fields = Hidden(await FormAsync(page, "telemetry-retention", token));
        fields["Input.LogDays"] = "10"; fields["Input.TraceDays"] = "5"; fields["Input.MetricRawDays"] = "20"; fields["Input.MetricRollupDays"] = "300";
        using var preview = await admin.PostAsync("/settings/service-reception/retention", new FormUrlEncodedContent(fields), token);
        Assert.Contains("確認儲存", await TextAsync(preview, token));
        var confirm = Hidden(await FormAsync(preview, "telemetry-retention", token));
        confirm["Input.LogDays"] = "10"; confirm["Input.TraceDays"] = "5"; confirm["Input.MetricRawDays"] = "20"; confirm["Input.MetricRollupDays"] = "300";
        using var saved = await admin.PostAsync("/settings/service-reception/retention", new FormUrlEncodedContent(confirm), token);
        Assert.Contains("保存設定已儲存", await TextAsync(saved, token));
        var current = await admin.GetFromJsonAsync<TelemetryRetentionSnapshot>("/api/v1/settings/telemetry/retention", token);
        Assert.AreEqual(10, current?.LogDays); Assert.AreEqual(5, current?.TraceDays);
        Assert.AreEqual(20, current?.MetricRawDays); Assert.AreEqual(300, current?.MetricRollupDays);
        using var audit = await admin.GetAsync("/settings/audit?source=telemetry-retention", token);
        Assert.Contains("應用 Logs 保存：14 → 10 天", await TextAsync(audit, token));
        Assert.Contains("應用 Traces 保存：7 → 5 天", await TextAsync(audit, token));
        Assert.Contains("應用指標原始資料保存：30 → 20 天", await TextAsync(audit, token));
    }, TestContext.CancellationToken);

    [TestMethod]
    public Task HostMetricRetentionAuthorizationAndForm() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        using var anonymous = Client(factory);
        using (var denied = await anonymous.GetAsync("/api/v1/settings/host-metrics/retention", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var reader = Client(factory); await LoginAsync(reader, "reader", token);
        using (var denied = await reader.GetAsync("/api/v1/settings/host-metrics/retention", token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using var admin = Client(factory); await LoginAsync(admin, "admin", token);
        var initial = await admin.GetFromJsonAsync<HostMetricRetentionSnapshot>("/api/v1/settings/host-metrics/retention", token);
        Assert.IsNotNull(initial);
        using (var noCsrf = await admin.PostAsJsonAsync("/api/v1/settings/host-metrics/retention/preview", new HostMetricRetentionPreviewRequest(10, 60, initial.Version), token))
            Assert.AreEqual(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using var page = await admin.GetAsync("/settings/agents/metric-retention", token);
        var fields = Hidden(await FormAsync(page, "host-metric-retention", token));
        fields["Input.RawDays"] = "10"; fields["Input.RollupDays"] = "60";
        using var preview = await admin.PostAsync("/settings/agents/metric-retention", new FormUrlEncodedContent(fields), token);
        Assert.Contains("確認儲存", await TextAsync(preview, token));
        var confirm = Hidden(await FormAsync(preview, "host-metric-retention", token));
        confirm["Input.RawDays"] = "10"; confirm["Input.RollupDays"] = "60";
        using var saved = await admin.PostAsync("/settings/agents/metric-retention", new FormUrlEncodedContent(confirm), token);
        Assert.Contains("保存設定已儲存", await TextAsync(saved, token));
        var current = await admin.GetFromJsonAsync<HostMetricRetentionSnapshot>("/api/v1/settings/host-metrics/retention", token);
        Assert.AreEqual(10, current?.RawDays); Assert.AreEqual(60, current?.RollupDays);
        using var audit = await admin.GetAsync("/settings/audit?source=host-metric-retention", token);
        Assert.Contains("原始樣本保存：30 → 10 天", await TextAsync(audit, token));
        Assert.Contains("五分鐘彙總保存：365 → 60 天", await TextAsync(audit, token));
    }, TestContext.CancellationToken);

    [TestMethod]
    public Task AuditRetentionAuthorizationAndForm() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token);
        using var anonymous = Client(factory);
        using (var denied = await anonymous.GetAsync("/api/v1/settings/audit-retention", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var reader = Client(factory); await LoginAsync(reader, "reader", token);
        using (var denied = await reader.GetAsync("/api/v1/settings/audit-retention", token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using var admin = Client(factory); await LoginAsync(admin, "admin", token);
        var initial = await admin.GetFromJsonAsync<AuditRetentionSnapshot>("/api/v1/settings/audit-retention", token);
        Assert.IsNotNull(initial);
        using (var noCsrf = await admin.PostAsJsonAsync("/api/v1/settings/audit-retention/preview", new AuditRetentionPreviewRequest(30, initial.Version), token))
            Assert.AreEqual(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using var page = await admin.GetAsync("/settings/audit/retention", token);
        var fields = Hidden(await FormAsync(page, "audit-retention", token));
        fields["Input.Days"] = "30";
        using var preview = await admin.PostAsync("/settings/audit/retention", new FormUrlEncodedContent(fields), token);
        Assert.Contains("確認儲存", await TextAsync(preview, token));
        var confirm = Hidden(await FormAsync(preview, "audit-retention", token));
        confirm["Input.Days"] = "30";
        using var saved = await admin.PostAsync("/settings/audit/retention", new FormUrlEncodedContent(confirm), token);
        Assert.Contains("保存設定已儲存", await TextAsync(saved, token));
        Assert.AreEqual(30, (await admin.GetFromJsonAsync<AuditRetentionSnapshot>("/api/v1/settings/audit-retention", token))?.Days);
        using var audit = await admin.GetAsync("/settings/audit?source=audit-retention", token);
        Assert.Contains("設定操作紀錄保存：365 → 30 天", await TextAsync(audit, token));
    }, TestContext.CancellationToken);

    [TestMethod]
    public Task SettingsAuthorizationAndActualPingDisplay() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        var device = await PingTestSite.InitializeAsync(factory, token);
        var network = factory.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>();
        using var anonymous = Client(factory);
        using (var denied = await anonymous.GetAsync($"/api/v1/devices/{device.Id:D}/ping", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var reader = Client(factory); await LoginAsync(reader, "reader", token);
        using (var denied = await reader.GetAsync("/api/v1/settings/ping", token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await reader.PutAsJsonAsync("/api/v1/settings/ping", new PingSettingsInput(30, 2, 3, 2, 64, ""), token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using var admin = Client(factory); await LoginAsync(admin, "admin", token);
        using (var noCsrf = await admin.PutAsJsonAsync("/api/v1/settings/ping", new PingSettingsInput(30, 2, 3, 2, 64, ""), token)) Assert.AreEqual(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using var settingsPage = await admin.GetAsync("/settings/ping", token);
        Assert.AreEqual(HttpStatusCode.OK, settingsPage.StatusCode);
        Assert.IsTrue(settingsPage.Headers.CacheControl?.NoStore);
        var fields = Hidden(await FormAsync(settingsPage, "ping-settings", token));
        fields["Input.Interval"] = "10"; fields["Input.Timeout"] = "10";
        fields["Input.Failures"] = "3"; fields["Input.Successes"] = "2"; fields["Input.Concurrency"] = "64";
        using (var invalid = await admin.PostAsync("/settings/ping", new FormUrlEncodedContent(fields), token))
            Assert.Contains("請檢查設定範圍", await TextAsync(invalid, token));
        fields["Input.Interval"] = "30"; fields["Input.Timeout"] = "2";
        using (var saved = await admin.PostAsync("/settings/ping", new FormUrlEncodedContent(fields), token))
            Assert.Contains("設定已儲存", await TextAsync(saved, token));
        using (var stale = await admin.PostAsync("/settings/ping", new FormUrlEncodedContent(fields), token))
            Assert.Contains("設定已被其他操作變更", await TextAsync(stale, token));
        var now = DateTimeOffset.UtcNow;
        var auditUrl = $"/api/v1/operations/audit/ping?from={Uri.EscapeDataString(now.AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(now.AddDays(1).ToString("O"))}";
        var approvalAuditUrl = $"/api/v1/operations/audit/service-approvals?from={Uri.EscapeDataString(now.AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(now.AddDays(1).ToString("O"))}";
        using (var denied = await anonymous.GetAsync(approvalAuditUrl, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using (var denied = await reader.GetAsync(approvalAuditUrl, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var allowed = await admin.GetAsync(approvalAuditUrl, token)) Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
        using (var denied = await anonymous.GetAsync(auditUrl, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using (var denied = await reader.GetAsync(auditUrl, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var response = await admin.GetAsync(auditUrl, token))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.IsTrue(response.Headers.CacheControl?.NoStore);
            var audit = await response.Content.ReadFromJsonAsync<OperationAuditPage>(cancellationToken: token);
            Assert.IsNotNull(audit);
            Assert.HasCount(1, audit.Items);
            Assert.AreEqual(30, audit.Items[0].PingSettings!.Current.IntervalSeconds);
        }
        using (var response = await admin.GetAsync("/settings/audit?source=ping", token))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            var auditText = await TextAsync(response, token);
            Assert.Contains("Ping 設定", auditText);
            Assert.Contains("並行上限", auditText);
            Assert.Contains("已儲存", auditText);
        }
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<PingSettings>();
            var store = scope.ServiceProvider.GetRequiredService<PingStore>();
            var runner = new PingRunner(settings, store, new SystemPingProbe(), TimeProvider.System);
            await store.ReconcileAsync(await settings.GetAsync(token), token);
            for (var round = 0; round < 2; round++)
            {
                await using (var db = await network.CreateDbContextAsync(token))
                    await db.Database.ExecuteSqlRawAsync("UPDATE network.PingTargets SET DueAt = DATEADD(second,-1,TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))", token);
                await runner.RunOnceAsync(token);
            }
        }
        var status = await reader.GetFromJsonAsync<DevicePingStatus>($"/api/v1/devices/{device.Id:D}/ping", token);
        Assert.IsNotNull(status); Assert.HasCount(1, status.Targets);
        Assert.AreEqual(Reachability.Reachable, status.Targets[0].State);
        Assert.AreEqual(PingOutcome.Success, status.Targets[0].LastOutcome);
        var dayStart = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var historyUrl = $"/api/v1/devices/{device.Id:D}/ping/history?from={Uri.EscapeDataString(dayStart.ToString("O"))}&to={Uri.EscapeDataString(dayStart.AddDays(1).ToString("O"))}&target=127.0.0.1&location=central";
        using (var deniedHistory = await anonymous.GetAsync(historyUrl, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, deniedHistory.StatusCode);
        using (var allowedHistory = await reader.GetAsync(historyUrl, token))
        {
            Assert.AreEqual(HttpStatusCode.OK, allowedHistory.StatusCode);
            Assert.IsTrue(allowedHistory.Headers.CacheControl?.NoStore);
            var history = await allowedHistory.Content.ReadFromJsonAsync<PingHistoryPage>(cancellationToken: token);
            Assert.IsNotNull(history);
            Assert.AreEqual(2L, history.Total);
            Assert.AreEqual(2L, history.Buckets.Sum(x => x.Successes));
        }
        using (var invalidHistory = await reader.GetAsync(historyUrl + "&page=0", token)) Assert.AreEqual(HttpStatusCode.BadRequest, invalidHistory.StatusCode);
        using var detail = await reader.GetAsync($"/devices/{device.Id:D}", token);
        var text = (await new HtmlParser().ParseDocumentAsync(await detail.Content.ReadAsStringAsync(token), token)).Body!.TextContent;
        Assert.Contains("可達", text); Assert.Contains("127.0.0.1", text);
        Assert.Contains("目前每 30 秒一次", text);
        Assert.DoesNotContain("編輯設備", text);
        using var list = await reader.GetAsync("/devices", token);
        var listText = (await new HtmlParser().ParseDocumentAsync(await list.Content.ReadAsStringAsync(token), token)).Body!.TextContent;
        Assert.Contains("主要目標 Ping", listText); Assert.Contains("可達", listText);

        using (var denied = await anonymous.GetAsync("/api/v1/settings/ping/retention", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using (var denied = await reader.GetAsync("/api/v1/settings/ping/retention", token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        var initialRetention = await admin.GetFromJsonAsync<PingRetentionSnapshot>("/api/v1/settings/ping/retention", token);
        Assert.IsNotNull(initialRetention);
        using (var noCsrf = await admin.PostAsJsonAsync("/api/v1/settings/ping/retention/preview", new PingRetentionPreviewRequest(20, 300, initialRetention.Version), token))
            Assert.AreEqual(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using var retentionPage = await admin.GetAsync("/settings/ping/retention", token);
        var retentionFields = Hidden(await FormAsync(retentionPage, "ping-retention", token));
        retentionFields["Input.RawDays"] = "20"; retentionFields["Input.RollupDays"] = "300";
        using var preview = await admin.PostAsync("/settings/ping/retention", new FormUrlEncodedContent(retentionFields), token);
        Assert.Contains("確認儲存", await TextAsync(preview, token));
        var confirmation = Hidden(await FormAsync(preview, "ping-retention", token));
        confirmation["Input.RawDays"] = "20"; confirmation["Input.RollupDays"] = "300";
        using var retentionSaved = await admin.PostAsync("/settings/ping/retention", new FormUrlEncodedContent(confirmation), token);
        Assert.Contains("保存設定已儲存", await TextAsync(retentionSaved, token));
        var savedRetention = await admin.GetFromJsonAsync<PingRetentionSnapshot>("/api/v1/settings/ping/retention", token);
        Assert.IsNotNull(savedRetention);
        Assert.AreEqual(20, savedRetention.RawDays);
        Assert.AreEqual(300, savedRetention.RollupDays);
        var historyWithRetention = await reader.GetFromJsonAsync<PingHistoryPage>(historyUrl, token);
        Assert.IsNotNull(historyWithRetention);
        Assert.AreEqual(20, historyWithRetention.RawRetentionDays);
        Assert.AreEqual(300, historyWithRetention.RollupRetentionDays);
        using var retentionAudit = await admin.GetAsync("/settings/audit?source=ping", token);
        Assert.Contains("原始 Ping 保存", await TextAsync(retentionAudit, token));
    }, TestContext.CancellationToken);

    private static async Task<string> TextAsync(HttpResponseMessage response, CancellationToken token) =>
        (await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token)).Body!.TextContent;

    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
    private static Dictionary<string, string> Hidden(IElement form) => form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
    private static async Task<IElement> FormAsync(HttpResponseMessage response, string name, CancellationToken token)
    {
        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token);
        return document.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == name);
    }
    private static async Task LoginAsync(HttpClient client, string name, CancellationToken token)
    {
        using var page = await client.GetAsync("/account/login", token);
        var fields = Hidden(await FormAsync(page, "login", token));
        fields["Input.UserName"] = name; fields["Input.Password"] = "Strong-Password-123";
        using var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(fields), token);
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
    }
}
