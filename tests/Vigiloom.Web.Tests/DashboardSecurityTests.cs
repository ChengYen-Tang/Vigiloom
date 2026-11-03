using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AngleSharp.Html.Parser;
using Hangfire;
using Hangfire.Common;
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
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Authentication;
using Vigiloom.Contracts.Operations;

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
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<FailingRecurringManager>(sp => new(new RecurringJobManager(sp.GetRequiredService<JobStorage>())));
                services.AddSingleton<IRecurringJobManager>(sp => sp.GetRequiredService<FailingRecurringManager>());
            });
        });
        await HangfireSchemaInstaller.InstallAsync(database.ConnectionString, token);
        var identity = factory.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>();
        var scheduling = factory.Services.GetRequiredService<IDbContextFactory<SchedulingDbContext>>();
        await using (var db = await identity.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        await using (var db = await scheduling.CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
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
        await using (var healthConnection = new SqlConnection(database.ConnectionString))
        {
            await healthConnection.OpenAsync(token);
            await using var command = healthConnection.CreateCommand();
            command.CommandText = """
                INSERT INTO [hangfire].[Server] (Id, Data, LastHeartbeat) VALUES
                  ('health-current', '{}', SYSUTCDATETIME()),
                  ('health-stale', '{}', DATEADD(minute, -5, SYSUTCDATETIME())),
                  ('health-unknown', '{}', DATEADD(minute, 5, SYSUTCDATETIME()))
                """;
            await command.ExecuteNonQueryAsync(token);
            using (var response = await admin.GetAsync("/api/v1/operations/platform-status", token))
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var scheduler = payload.RootElement.GetProperty("scheduler");
                Assert.AreEqual(1, scheduler.GetProperty("current").GetInt32());
                Assert.AreEqual(1, scheduler.GetProperty("stale").GetInt32());
                Assert.AreEqual(1, scheduler.GetProperty("unknown").GetInt32());
                Assert.AreEqual(0, scheduler.GetProperty("state").GetInt32());
            }
            command.CommandText = "UPDATE [hangfire].[Server] SET LastHeartbeat = SYSUTCDATETIME() WHERE Id LIKE 'health-%'";
            await command.ExecuteNonQueryAsync(token);
            using (var response = await admin.GetAsync("/api/v1/operations/platform-status", token))
            {
                using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var scheduler = payload.RootElement.GetProperty("scheduler");
                Assert.AreEqual(3, scheduler.GetProperty("current").GetInt32());
                Assert.AreEqual(1, scheduler.GetProperty("state").GetInt32());
            }
            command.CommandText = "DELETE FROM [hangfire].[Server] WHERE Id LIKE 'health-%'";
            await command.ExecuteNonQueryAsync(token);
        }
        await using (var seedConnection = new SqlConnection(database.ConnectionString))
        {
            await seedConnection.OpenAsync(token);
            await using var command = seedConnection.CreateCommand();
            var bootRows = Enumerable.Range(1, 64).Select(slot => $"({slot}, @boot{slot})").ToArray();
            command.CommandText = $"""
                UPDATE slot SET BootId = assigned.BootId,
                    ObservedAt = DATEADD(minute, -10, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')),
                    LeaseUntil = DATEADD(second, CASE WHEN slot.Slot = 1 THEN 120 ELSE -1 END, TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))
                FROM operations.WorkerDiagnosticSlots slot
                INNER JOIN (VALUES {string.Join(",", bootRows)}) assigned(Slot, BootId) ON assigned.Slot = slot.Slot
                """;
            for (var slot = 1; slot <= 64; slot++) command.Parameters.AddWithValue($"@boot{slot}", Guid.CreateVersion7());
            Assert.AreEqual(64, await command.ExecuteNonQueryAsync(token));
            command.Parameters.Clear();
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
            Assert.Contains("平台健康", text);
            Assert.Contains("目前沒有心跳資料", text);
            Assert.Contains("保留範圍內沒有成功批次紀錄", text);
            Assert.Contains("Worker 掃描摘要", text);
            Assert.DoesNotContain("診斷槽位已達上限", text);
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
        var now = DateTimeOffset.UtcNow;
        var feedUrl = $"/api/v1/operations/audit-feed/scheduling?from={Uri.EscapeDataString(now.AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(now.AddDays(1).ToString("O"))}";
        var feed = await admin.GetFromJsonAsync<AuditFeedPage>(feedUrl, token);
        Assert.IsNotNull(feed);
        Assert.IsTrue(feed.Items.Any(item => item.Action == AuditFeedAction.JobDeleteRequested &&
            item.Outcome == AuditFeedOutcome.Accepted && item.Reference == jobId));

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
            Assert.DoesNotContain("無法確認儲存結果", text);
            fields[form.QuerySelector("select")!.GetAttribute("name")!] = "15";
            using var stale = await admin.PostAsync("/settings/jobs", new FormUrlEncodedContent(fields), token);
            var staleDocument = await new HtmlParser().ParseDocumentAsync(await stale.Content.ReadAsStringAsync(token), token);
            Assert.Contains("設定已被其他管理員修改", staleDocument.Body!.TextContent);
            var staleForm = staleDocument.QuerySelectorAll("form").Single(element => element.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "job-schedule");
            Assert.IsTrue(staleForm.QuerySelector("button[type=submit]")!.HasAttribute("disabled"));
        }
        using (var scope = factory.Services.CreateScope())
            Assert.AreEqual(5, scope.ServiceProvider.GetRequiredService<OutboxScheduleManager>().GetSnapshot().Minutes);
        using (var settingsPage = await admin.GetAsync("/settings/jobs", token))
        {
            var document = await new HtmlParser().ParseDocumentAsync(await settingsPage.Content.ReadAsStringAsync(token), token);
            var form = document.QuerySelectorAll("form").Single(element => element.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "job-schedule");
            var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
            var intervalName = form.QuerySelector("select")!.GetAttribute("name")!;
            fields[intervalName] = "10";
            factory.Services.GetRequiredService<FailingRecurringManager>().FailNext = true;
            using var pending = await admin.PostAsync("/settings/jobs", new FormUrlEncodedContent(fields), token);
            var pendingDocument = await new HtmlParser().ParseDocumentAsync(await pending.Content.ReadAsStringAsync(token), token);
            Assert.Contains("設定已儲存，正在等待背景服務套用", pendingDocument.Body!.TextContent);
            var pendingForm = pendingDocument.QuerySelectorAll("form").Single(element => element.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "job-schedule");
            Assert.IsFalse(pendingForm.QuerySelector("button[type=submit]")!.HasAttribute("disabled"));
            fields = pendingForm.QuerySelectorAll("input[type=hidden][name]").ToDictionary(element => element.GetAttribute("name")!, element => element.GetAttribute("value") ?? "");
            fields[intervalName] = "15";
            using var retry = await admin.PostAsync("/settings/jobs", new FormUrlEncodedContent(fields), token);
            var retryText = (await new HtmlParser().ParseDocumentAsync(await retry.Content.ReadAsStringAsync(token), token)).Body!.TextContent;
            Assert.Contains("背景工作檢查頻率已儲存", retryText);
            Assert.DoesNotContain("設定已被其他管理員修改", retryText);
        }
        await using (var audit = connection.CreateCommand())
        {
            audit.CommandText = "SELECT COUNT(*) FROM scheduling.DashboardAudit WHERE Action='/settings/jobs/schedule' AND Result='accepted'";
            Assert.AreEqual(2, (int)(await audit.ExecuteScalarAsync(token))!);
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

    private sealed class FailingRecurringManager(IRecurringJobManager inner) : IRecurringJobManager
    {
        public bool FailNext { get; set; }
        public void AddOrUpdate(string recurringJobId, Job job, string cronExpression, RecurringJobOptions options)
        {
            if (FailNext) { FailNext = false; throw new InvalidOperationException("Simulated schedule update failure."); }
            inner.AddOrUpdate(recurringJobId, job, cronExpression, options);
        }
        public void Trigger(string recurringJobId) => inner.Trigger(recurringJobId);
        public void RemoveIfExists(string recurringJobId) => inner.RemoveIfExists(recurringJobId);
    }

    public static void Probe() { }
}
