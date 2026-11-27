using System.Net;
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
    public Task SettingsPageRequiresAdministratorAndHidesCredentials() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        using var anonymous = PingTestSite.Client(factory);
        using var reader = PingTestSite.Client(factory);
        using var admin = PingTestSite.Client(factory);
        await PingTestSite.LoginAsync(reader, "reader", token);
        await PingTestSite.LoginAsync(admin, "admin", token);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var channel = scope.ServiceProvider.GetRequiredService<LineChannel>();
            Assert.IsTrue(await channel.SaveAsync(new(true, "sensitive-token", "sensitive-secret"), Guid.CreateVersion7(), token));
            await channel.RecordCandidatesAsync(["Csettings-test"], token);
            var groupId = (await channel.GetAsync(token)).Groups.Single().Id;
            Assert.IsTrue(await channel.SetGroupAsync(groupId, "Ops", true, Guid.CreateVersion7(), token));
        }
        using (var deniedPage = await anonymous.GetAsync("/settings/line", token))
            Assert.AreEqual(HttpStatusCode.Redirect, deniedPage.StatusCode);
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
    }, context.CancellationToken);

}
