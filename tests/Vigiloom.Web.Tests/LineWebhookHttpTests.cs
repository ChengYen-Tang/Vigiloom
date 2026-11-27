using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Modules.AlertingOperations.Application.Line;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Persistence;
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

    [TestMethod]
    public Task WebhookRequiresSignatureAndOnlyDiscoversCandidates() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        const string secret = "webhook-test-secret";
        await using (var scope = factory.Services.CreateAsyncScope())
            Assert.IsTrue(await scope.ServiceProvider.GetRequiredService<LineChannel>().SaveAsync(new(false, "test-access-token", secret), Guid.CreateVersion7(), token));

        var bytes = Encoding.UTF8.GetBytes("""
            {"destination":"Utest","events":[
              {"type":"join","source":{"type":"group","groupId":"Cfirst"}},
              {"type":"message","source":{"type":"group","groupId":"Csecond"},"message":{"id":"1","type":"text","text":"bind"}}
            ]}
            """);
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), bytes));
        using var client = PingTestSite.Client(factory);
        async Task<HttpStatusCode> PostAsync(string header)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/line/webhook");
            request.Content = new ByteArrayContent(bytes);
            request.Headers.Add("x-line-signature", header);
            using var response = await client.SendAsync(request, token);
            return response.StatusCode;
        }
        Assert.AreEqual(HttpStatusCode.Unauthorized, await PostAsync("invalid"));
        var malformed = Encoding.UTF8.GetBytes("{\"events\":[");
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/line/webhook"))
        {
            request.Content = new ByteArrayContent(malformed);
            request.Headers.Add("x-line-signature", Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), malformed)));
            using var response = await client.SendAsync(request, token);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }
        await using (var before = await factory.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token))
            Assert.AreEqual(0, await before.Set<LineGroup>().CountAsync(token));
        Assert.AreEqual(HttpStatusCode.OK, await PostAsync(signature));
        Assert.AreEqual(HttpStatusCode.OK, await PostAsync(signature));
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token);
        var groups = await db.Set<LineGroup>().ToArrayAsync(token);
        Assert.HasCount(2, groups);
        Assert.IsTrue(groups.All(group => !group.Approved));
        CollectionAssert.AreEquivalent(new[] { "Cfirst", "Csecond" }, groups.Select(group => group.ExternalId).ToArray());
    }, context.CancellationToken);
}
