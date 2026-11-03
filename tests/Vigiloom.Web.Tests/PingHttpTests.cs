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
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
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
        using var detail = await reader.GetAsync($"/devices/{device.Id:D}", token);
        var text = (await new HtmlParser().ParseDocumentAsync(await detail.Content.ReadAsStringAsync(token), token)).Body!.TextContent;
        Assert.Contains("可達", text); Assert.Contains("127.0.0.1", text);
        Assert.Contains("目前每 30 秒一次", text);
        Assert.DoesNotContain("編輯設備", text);
        using var list = await reader.GetAsync("/devices", token);
        var listText = (await new HtmlParser().ParseDocumentAsync(await list.Content.ReadAsStringAsync(token), token)).Body!.TextContent;
        Assert.Contains("主要目標 Ping", listText); Assert.Contains("可達", listText);
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
