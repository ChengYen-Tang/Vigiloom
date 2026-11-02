using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Persistence;
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

    private static async Task<Dictionary<string, string>> FieldsAsync(HttpResponseMessage response, CancellationToken token)
    {
        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token);
        var form = document.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "agent-settings");
        return form.QuerySelectorAll("input[name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
    }

    private static async Task<string> BodyTextAsync(HttpResponseMessage response, CancellationToken token) =>
        (await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token)).Body!.TextContent;
}
