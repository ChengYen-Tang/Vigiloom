using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Modules.AlertingOperations.Application.Line;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class LineWebhookHttpTests(TestContext context)
{
    [TestMethod]
    public Task SettingsRequireAdministratorAndAntiforgery() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        using var anonymous = PingTestSite.Client(factory);
        using var reader = PingTestSite.Client(factory);
        using var admin = PingTestSite.Client(factory);
        await PingTestSite.LoginAsync(reader, "reader", token);
        await PingTestSite.LoginAsync(admin, "admin", token);
        const string route = "/api/v1/admin/line/";
        var update = new { Enabled = false, AccessToken = "sensitive-token", ChannelSecret = "sensitive-secret" };
        using (var response = await anonymous.PutAsJsonAsync(route, update, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        using (var response = await reader.PutAsJsonAsync(route, update, token)) Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        using (var response = await admin.PutAsJsonAsync(route, update, token)) Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        using (var anti = await admin.GetAsync("/api/v1/session/antiforgery", token))
        {
            var json = await anti.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: token);
            admin.DefaultRequestHeaders.Add("RequestVerificationToken", json.GetProperty("requestToken").GetString());
        }
        using (var response = await admin.PutAsJsonAsync(route, update, token)) Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        using (var response = await admin.PutAsJsonAsync(route, new { Enabled = true }, token)) Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        using (var response = await admin.PutAsJsonAsync(route, new { AccessToken = "replacement" }, token)) Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Guid groupId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var channel = scope.ServiceProvider.GetRequiredService<LineChannel>();
            await channel.RecordCandidatesAsync(["Csettings-test"], token);
            groupId = (await channel.GetAsync(token)).Groups.Single().Id;
        }
        using (var response = await admin.PutAsJsonAsync($"{route}groups/{groupId:D}", new { Name = "Ops", Approved = true }, token)) Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
        using (var response = await admin.PutAsJsonAsync($"{route}groups/{groupId:D}", new { Name = "Renamed" }, token)) Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        using (var deniedPage = await reader.GetAsync("/settings/line", token))
            Assert.AreEqual(HttpStatusCode.Redirect, deniedPage.StatusCode);
        admin.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        using (var english = await admin.GetAsync("/settings/line", token))
        {
            var html = WebUtility.HtmlDecode(await english.Content.ReadAsStringAsync(token));
            Assert.Contains("LINE notification settings", html);
            Assert.Contains("Ops", html);
            Assert.Contains("does not mean the message was delivered", html);
            Assert.DoesNotContain("sensitive-token", html);
            Assert.DoesNotContain("sensitive-secret", html);
        }
        admin.DefaultRequestHeaders.AcceptLanguage.Clear();
        admin.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-Hans");
        using (var simplified = await admin.GetAsync("/settings/line", token))
            Assert.Contains("LINE 通知设置", WebUtility.HtmlDecode(await simplified.Content.ReadAsStringAsync(token)));
        admin.DefaultRequestHeaders.AcceptLanguage.Clear();
        using var settings = await admin.GetAsync(route, token);
        var body = await settings.Content.ReadAsStringAsync(token);
        Assert.AreEqual(HttpStatusCode.OK, settings.StatusCode);
        Assert.DoesNotContain("sensitive-token", body);
        Assert.DoesNotContain("sensitive-secret", body);
        using var parsed = JsonDocument.Parse(body);
        Assert.IsTrue(parsed.RootElement.GetProperty("enabled").GetBoolean());
        Assert.IsTrue(parsed.RootElement.GetProperty("groups")[0].GetProperty("approved").GetBoolean());
        using var denied = await reader.GetAsync(route, token);
        Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
    }, context.CancellationToken);

}
