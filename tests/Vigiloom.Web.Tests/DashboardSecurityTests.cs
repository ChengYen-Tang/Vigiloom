using System.Net;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Hangfire;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Authentication;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[DoNotParallelize]
public sealed class DashboardSecurityTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task Dashboard_EnforcesRolesCsrfAndDurableAudit() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        const string setupSecret = "test-only-bootstrap-secret-with-adequate-length";
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Vigiloom"] = database.ConnectionString,
                ["Vigiloom:SetupToken"] = setupSecret
            }));
        });
        await HangfireSchemaInstaller.InstallAsync(database.ConnectionString, token);
        var identity = factory.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
        var scheduling = factory.Services.GetRequiredService<IDbContextFactory<SchedulingDbContext>>();
        await using (var db = await identity.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        await using (var db = await scheduling.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        await RoleInitializer.InitializeAsync(identity, token);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.AreEqual(InitialAdministratorStatus.Created, (await scope.ServiceProvider.GetRequiredService<InitialAdministrator>()
                .CreateAsync(setupSecret, "admin", "Strong-Password-123", token)).Status);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
            var reader = new Account { UserName = "reader" };
            Assert.IsTrue((await users.CreateAsync(reader, "Reader-Password-123")).Succeeded);
            Assert.IsTrue((await users.AddToRoleAsync(reader, BuiltInRoles.ReadOnly)).Succeeded);
        }
        using var anonymous = Client(factory);
        using (var response = await anonymous.GetAsync("/admin/jobs/", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        using (var response = await anonymous.GetAsync("/settings/jobs", token)) Assert.AreNotEqual(HttpStatusCode.OK, response.StatusCode);
        using var readerClient = Client(factory);
        await LoginAsync(readerClient, "reader", "Reader-Password-123", token);
        foreach (var path in new[] { "/admin/jobs/", "/admin/jobs/stats", "/admin/jobs/js123" })
            using (var response = await readerClient.GetAsync(path, token)) Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        using (var response = await readerClient.GetAsync("/settings/jobs", token)) Assert.AreNotEqual(HttpStatusCode.OK, response.StatusCode);
        using var admin = Client(factory);
        await LoginAsync(admin, "admin", "Strong-Password-123", token);
        await using (var seedConnection = new SqlConnection(database.ConnectionString))
        {
            await seedConnection.OpenAsync(token);
            await using var command = seedConnection.CreateCommand();
            command.CommandText = """
                INSERT INTO scheduling.Outbox(MessageId, MessageType, Payload, CreatedAt, AvailableAt, State, Attempts, LastAttemptAt, LastFailureCode)
                VALUES (@id, 'test.effect.v1', '{}', TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'),
                        TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'), 3, 10, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'), 'handler_failed')
                """;
            command.Parameters.AddWithValue("@id", Guid.CreateVersion7());
            await command.ExecuteNonQueryAsync(token);
        }
        using (var overview = await admin.GetAsync("/settings/jobs", token))
        {
            Assert.AreEqual(HttpStatusCode.OK, overview.StatusCode);
            Assert.IsTrue(overview.Headers.CacheControl?.NoStore);
            var html = await overview.Content.ReadAsStringAsync(token);
            var text = (await new HtmlParser().ParseDocumentAsync(html, token)).Body!.TextContent;
            Assert.Contains("背景工作", text);
            Assert.Contains("背景資料處理失敗", text);
            Assert.Contains("已停止重試", text);
            Assert.DoesNotContain("test.effect.v1", html);
        }
        using (var page = await admin.GetAsync("/admin/jobs/", token))
        {
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
            Assert.IsTrue(page.Headers.CacheControl?.NoStore);
        }
        using (var get = await admin.GetAsync("/admin/jobs/jobs/failed/requeue", token)) Assert.AreEqual(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        using (var get = await admin.GetAsync("/admin/jobs/JOBS/FAILED/REQUEUE/", token)) Assert.AreEqual(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        using (var forged = await admin.PostAsync("/admin/jobs/jobs/failed/requeue", new FormUrlEncodedContent(new Dictionary<string, string> { ["jobs[]"] = "1" }), token))
            Assert.AreEqual(HttpStatusCode.Forbidden, forged.StatusCode);

        var jobId = factory.Services.GetRequiredService<IBackgroundJobClient>().Enqueue(() => Probe());
        using var tokenResponse = await admin.GetAsync("/api/v1/session/antiforgery", token);
        using var json = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(token));
        var requestToken = json.RootElement.GetProperty("requestToken").GetString()!;
        using (var statistics = await admin.PostAsync("/admin/jobs/stats", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = requestToken
        }), token)) Assert.AreEqual(HttpStatusCode.OK, statistics.StatusCode);
        using (var response = await admin.PostAsync("/admin/jobs/jobs/enqueued/delete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["jobs[]"] = jobId, ["__RequestVerificationToken"] = requestToken
        }), token)) Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);

        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync(token);
        await using (var audit = connection.CreateCommand())
        {
            audit.CommandText = "SELECT Action, JobId, Result FROM scheduling.DashboardAudit";
            await using var rows = await audit.ExecuteReaderAsync(token);
            Assert.IsTrue(await rows.ReadAsync(token));
            Assert.AreEqual("/jobs/enqueued/delete", rows.GetString(0));
            Assert.AreEqual(jobId, rows.GetString(1));
            Assert.AreEqual("accepted", rows.GetString(2));
            Assert.IsFalse(await rows.ReadAsync(token));
        }
        using (var settingsPage = await admin.GetAsync("/settings/jobs", token))
        {
            var document = await new HtmlParser().ParseDocumentAsync(await settingsPage.Content.ReadAsStringAsync(token), token);
            var form = document.QuerySelectorAll("form").Single(element => element.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "job-schedule");
            var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
            fields[form.QuerySelector("select")!.GetAttribute("name")!] = "5";
            using var changed = await admin.PostAsync("/settings/jobs", new FormUrlEncodedContent(fields), token);
            Assert.AreEqual(HttpStatusCode.OK, changed.StatusCode);
            var text = (await new HtmlParser().ParseDocumentAsync(await changed.Content.ReadAsStringAsync(token), token)).Body!.TextContent;
            Assert.Contains("背景工作檢查頻率已儲存", text);
            fields[form.QuerySelector("select")!.GetAttribute("name")!] = "15";
            using var stale = await admin.PostAsync("/settings/jobs", new FormUrlEncodedContent(fields), token);
            var staleText = (await new HtmlParser().ParseDocumentAsync(await stale.Content.ReadAsStringAsync(token), token)).Body!.TextContent;
            Assert.Contains("設定已被其他管理員修改", staleText);
        }
        using (var scope = factory.Services.CreateScope())
            Assert.AreEqual(5, scope.ServiceProvider.GetRequiredService<OutboxScheduleManager>().GetSnapshot().Minutes);
        await using (var audit = connection.CreateCommand())
        {
            audit.CommandText = "SELECT COUNT(*) FROM scheduling.DashboardAudit WHERE Action='/settings/jobs/schedule' AND Result='accepted'";
            Assert.AreEqual(1, (int)(await audit.ExecuteScalarAsync(token))!);
        }
        await using (var drop = connection.CreateCommand())
        {
            drop.CommandText = "DROP TABLE scheduling.DashboardAudit";
            await drop.ExecuteNonQueryAsync(token);
        }
        using var blocked = await admin.PostAsync("/admin/jobs/jobs/enqueued/delete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["jobs[]"] = jobId, ["__RequestVerificationToken"] = requestToken
        }), token);
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
    }, TestContext.CancellationToken);

    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost")
    });

    private static async Task LoginAsync(HttpClient client, string user, string password, CancellationToken token)
    {
        using var page = await client.GetAsync("/account/login", token);
        var document = await new HtmlParser().ParseDocumentAsync(await page.Content.ReadAsStringAsync(token), token);
        var form = document.QuerySelectorAll("form").Single(element => element.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "login");
        var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
        fields["Input.UserName"] = user;
        fields["Input.Password"] = password;
        using var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(fields), token);
        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
    }

    public static void Probe() { }
}
