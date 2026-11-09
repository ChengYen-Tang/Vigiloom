using System.Net;
using System.Net.Http.Json;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Contracts.Operations;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Modules.NetworkMonitoring.Domain.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Data.Common;
using Vigiloom.BuildingBlocks.Operations;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Microsoft.EntityFrameworkCore.Storage;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Persistence;

namespace Vigiloom.Web.Tests;
[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[DoNotParallelize]
public sealed class SpeedHttpTests
{
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    public Task AgentInboxSettings_RequiresAdministratorCsrfAndPreview() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
        {
            await db.Database.MigrateAsync(token);
            await db.Database.ExecuteSqlRawAsync("DECLARE @now datetimeoffset = TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'); UPDATE [agent].[ReplayPolicies] SET [Phase] = 2, [FreshInstallation] = 1, [ActivatedAt] = @now, [EnforcedAt] = @now, [CleanupCutoverAt] = @now, [InboxSettingsChangedAt] = @now, [LegacyReceiptsRetainUntil] = DATEADD(day, 7, @now) WHERE [Scope] = 1", token);
        }
        const string url = "/api/v1/settings/agent-inbox/";
        using var anonymous = Client(factory);
        using (var denied = await anonymous.GetAsync(url, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var reader = Client(factory); await LoginAsync(reader, "reader", token);
        using (var denied = await reader.GetAsync(url, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using var admin = Client(factory); await LoginAsync(admin, "admin", token);
        var before = await admin.GetFromJsonAsync<AgentInboxSettingsSnapshot>(url, token);
        Assert.IsNotNull(before);
        Assert.IsTrue(before.CleanupReady);
        using (var forged = await admin.PostAsJsonAsync(url + "preview", new AgentInboxPreviewRequest(3600, 2, before.Version), token)) Assert.AreEqual(HttpStatusCode.BadRequest, forged.StatusCode);
        using (var anti = await admin.GetAsync("/api/v1/session/antiforgery", token))
        {
            using var json = System.Text.Json.JsonDocument.Parse(await anti.Content.ReadAsStringAsync(token));
            admin.DefaultRequestHeaders.Add("RequestVerificationToken", json.RootElement.GetProperty("requestToken").GetString());
        }
        using (var unconfirmed = await admin.PutAsJsonAsync(url, new AgentInboxSettingsUpdate(3600, 2, before.Version, null), token)) Assert.AreEqual(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        using var previewResponse = await admin.PostAsJsonAsync(url + "preview", new AgentInboxPreviewRequest(3600, 2, before.Version), token);
        Assert.AreEqual(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = await previewResponse.Content.ReadFromJsonAsync<AgentInboxSettingsPreview>(cancellationToken: token);
        Assert.IsNotNull(preview);
        using (var saved = await admin.PutAsJsonAsync(url, new AgentInboxSettingsUpdate(3600, 2, before.Version, preview.ConfirmationToken), token)) Assert.AreEqual(HttpStatusCode.NoContent, saved.StatusCode);
        using (var stale = await admin.PutAsJsonAsync(url, new AgentInboxSettingsUpdate(3600, 2, before.Version, preview.ConfirmationToken), token)) Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
        var after = await admin.GetFromJsonAsync<AgentInboxSettingsSnapshot>(url, token);
        Assert.AreEqual(3600, after!.ReplaySeconds);
        Assert.AreEqual(2, after.RetentionDays);
        var now = DateTimeOffset.UtcNow;
        var auditUrl = $"/api/v1/operations/audit/agent-inbox?from={Uri.EscapeDataString(now.AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(now.AddDays(1).ToString("O"))}";
        var audit = await admin.GetFromJsonAsync<OperationAuditPage>(auditUrl, token);
        Assert.AreEqual(3600, audit!.Items.Single().AgentInboxSettings!.CurrentReplaySeconds);
    }, TestContext.CancellationToken);

    [TestMethod]
    public Task AgentInboxAudit_IsTypedAndAdministratorOnly() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token);
        var now = DateTimeOffset.UtcNow;
        var version = Convert.ToBase64String(new byte[8]);
        var details = new AgentInboxSettingsChanged(86400, 7, 3600, 2, version, version);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token))
        {
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            await new OperationAuditWriter().AppendAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
                new OperationAudit(Guid.CreateVersion7(), Guid.CreateVersion7(), now, details), token);
            await transaction.CommitAsync(token);
        }
        var range = $"?from={Uri.EscapeDataString(now.AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(now.AddDays(1).ToString("O"))}";
        var url = "/api/v1/operations/audit/agent-inbox" + range;
        using var anonymous = Client(factory);
        using (var denied = await anonymous.GetAsync(url, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var reader = Client(factory); await LoginAsync(reader, "reader", token);
        using (var denied = await reader.GetAsync(url, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using var admin = Client(factory); await LoginAsync(admin, "admin", token);
        var page = await admin.GetFromJsonAsync<OperationAuditPage>(url, token);
        Assert.IsNotNull(page);
        Assert.AreEqual(details, page.Items.Single().AgentInboxSettings);
        var speedPage = await admin.GetFromJsonAsync<OperationAuditPage>("/api/v1/operations/audit" + range, token);
        Assert.IsNotNull(speedPage);
        Assert.IsFalse(speedPage.Items.Any(x => x.AgentInboxSettings is not null));
    }, TestContext.CancellationToken);
    [TestMethod]
    public Task ScheduleSettingsAreProtectedAndManualStartIsUnavailable() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token);
        var now = DateTimeOffset.UtcNow;
        var auditUrl = $"/api/v1/operations/audit?from={Uri.EscapeDataString(now.AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(now.AddDays(1).ToString("O"))}";
        var feedUrl = $"/api/v1/operations/audit-feed/accounts?from={Uri.EscapeDataString(now.AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(now.AddDays(1).ToString("O"))}";
        using var anonymous = Client(factory);
        using (var denied = await anonymous.GetAsync(auditUrl, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using (var denied = await anonymous.GetAsync(feedUrl, token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using (var denied = await anonymous.GetAsync("/api/v1/operations/platform-status", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using (var denied = await anonymous.GetAsync("/api/v1/speed-tests?from=2026-11-01T00:00:00Z&to=2026-11-02T00:00:00Z", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var reader = Client(factory); await LoginAsync(reader, "reader", token);
        using (var denied = await reader.GetAsync(auditUrl, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await reader.GetAsync(feedUrl, token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await reader.GetAsync("/api/v1/operations/platform-status", token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await reader.GetAsync("/settings/audit", token))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, denied.StatusCode);
            Assert.Contains("/account/access-denied", denied.Headers.Location?.OriginalString ?? "");
        }
        using (var denied = await reader.GetAsync("/api/v1/settings/speed-tests", token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await reader.PostAsJsonAsync("/api/v1/speed-tests", new { }, token)) Assert.AreEqual(HttpStatusCode.MethodNotAllowed, denied.StatusCode);
        using var admin = Client(factory); await LoginAsync(admin, "admin", token);
        using (var feed = await admin.GetAsync(feedUrl, token)) Assert.AreEqual(HttpStatusCode.OK, feed.StatusCode);
        using (var invalid = await admin.GetAsync(feedUrl.Replace("/accounts?", "/unsupported?"), token)) Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var health = await admin.GetAsync("/api/v1/operations/platform-status", token))
        {
            Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
            using var document = System.Text.Json.JsonDocument.Parse(await health.Content.ReadAsStringAsync(token));
            Assert.AreEqual(1, document.RootElement.GetProperty("database").GetInt32());
            Assert.IsFalse(document.RootElement.GetProperty("agentQueryAvailable").GetBoolean());
            Assert.IsFalse(document.RootElement.GetProperty("scheduler").GetProperty("available").GetBoolean());
        }
        var first = await admin.GetFromJsonAsync<SpeedSettingsSnapshot>("/api/v1/settings/speed-tests", token);
        Assert.IsNotNull(first); Assert.IsTrue(first.Automatic); Assert.IsNotNull(first.NextAutomaticAt);
        using (var csrf = await admin.PutAsJsonAsync("/api/v1/settings/speed-tests", new SpeedSettingsInput(false, 6, 2, 40, first.Version), token)) Assert.AreEqual(HttpStatusCode.BadRequest, csrf.StatusCode);
        using var page = await admin.GetAsync("/settings/speed-tests", token);
        Assert.IsTrue(page.Headers.CacheControl?.NoStore);
        var form = await FormAsync(page, "speed-settings", token);
        var fields = Hidden(form);
        fields["Input.Hours"] = "6"; fields["Input.MaxMiB"] = "2"; fields["Input.Timeout"] = "40";
        fields["Input.Automatic"] = "false";
        using (var save = await admin.PostAsync("/settings/speed-tests", new FormUrlEncodedContent(fields), token))
            Assert.Contains("設定已儲存", await TextAsync(save, token));
        using (var stale = await admin.PostAsync("/settings/speed-tests", new FormUrlEncodedContent(fields), token))
            Assert.Contains("設定已被其他操作變更", await TextAsync(stale, token));
        var updated = await admin.GetFromJsonAsync<SpeedSettingsSnapshot>("/api/v1/settings/speed-tests", token);
        Assert.IsNotNull(updated); Assert.IsFalse(updated.Automatic); Assert.AreEqual(2, updated.MaxMiB);
        using (var auditResponse = await admin.GetAsync(auditUrl, token))
        {
            Assert.AreEqual(HttpStatusCode.OK, auditResponse.StatusCode);
            Assert.IsTrue(auditResponse.Headers.CacheControl?.NoStore);
            var audit = await auditResponse.Content.ReadFromJsonAsync<OperationAuditPage>(cancellationToken: token);
            Assert.IsNotNull(audit);
            Assert.HasCount(1, audit.Items);
            Assert.AreEqual(2, audit.Items[0].SpeedSettings!.Current.MaxMiB);
        }
        using (var auditPage = await admin.GetAsync("/settings/audit", token))
        {
            Assert.AreEqual(HttpStatusCode.OK, auditPage.StatusCode);
            var text = await TextAsync(auditPage, token);
            Assert.Contains("網路測速設定", text);
            Assert.Contains("admin", text);
            Assert.Contains("已儲存", text);
            Assert.Contains("其他來源可由上方選擇", text);
        }
        using (var invalidAudit = await admin.GetAsync(auditUrl + "&page=1001", token))
            Assert.AreEqual(HttpStatusCode.BadRequest, invalidAudit.StatusCode);
        using (var invalidPage = await admin.GetAsync("/settings/audit?page=abc", token))
            Assert.Contains("頁碼無效，請重新查詢", await TextAsync(invalidPage, token));
        using (var invertedDates = await admin.GetAsync("/settings/audit?from=2026-02-02&to=2026-02-01", token))
        {
            var document = await new HtmlParser().ParseDocumentAsync(await invertedDates.Content.ReadAsStringAsync(token), token);
            Assert.Contains("開始日期不能晚於結束日期", document.Body!.TextContent);
            Assert.AreEqual("audit-date-error", document.QuerySelector("#audit-from")?.GetAttribute("aria-describedby"));
        }
        using (var longRange = await admin.GetAsync("/settings/audit?from=2026-01-01&to=2026-02-01", token))
            Assert.Contains("一次最多查詢 31 天", await TextAsync(longRange, token));
        using (var empty = await admin.GetAsync("/settings/audit?from=2000-01-01&to=2000-01-02", token))
        {
            var html = await TextAsync(empty, token);
            Assert.Contains("此日期範圍沒有稽核紀錄", html);
            Assert.DoesNotContain("第 1 頁", html);
        }
        using (var emptyLaterPage = await admin.GetAsync("/settings/audit?from=2000-01-01&to=2000-01-02&page=2", token))
        {
            var text = await TextAsync(emptyLaterPage, token);
            Assert.Contains("此頁沒有紀錄，請返回上一頁", text);
            Assert.Contains("上一頁", text);
        }
        using var response = await admin.PostAsJsonAsync("/api/v1/speed-tests", new { }, token);
        Assert.AreEqual(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        using var invalidRange = await reader.GetAsync("/api/v1/speed-tests?from=2026-01-01T00:00:00Z&to=2026-11-01T00:00:00Z", token);
        Assert.AreEqual(HttpStatusCode.BadRequest, invalidRange.StatusCode);
    }, TestContext.CancellationToken);

    [TestMethod]
    public Task RetentionSettings_RequireAdministratorAndPreviewBeforeConfirmation() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token);
        using var anonymous = Client(factory);
        using (var denied = await anonymous.GetAsync("/api/v1/settings/speed-retention", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var reader = Client(factory); await LoginAsync(reader, "reader", token);
        using (var denied = await reader.GetAsync("/api/v1/settings/speed-retention", token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await reader.PostAsJsonAsync("/api/v1/settings/speed-retention/preview", new SpeedRetentionPreviewRequest(30, "invalid"), token)) Assert.AreEqual(HttpStatusCode.Forbidden, denied.StatusCode);
        using var admin = Client(factory); await LoginAsync(admin, "admin", token);
        var beforeRetention = await admin.GetFromJsonAsync<SpeedRetentionSnapshot>("/api/v1/settings/speed-retention", token);
        Assert.IsNotNull(beforeRetention);
        using (var denied = await admin.PostAsJsonAsync("/api/v1/settings/speed-retention/preview", new SpeedRetentionPreviewRequest(30, beforeRetention.Version), token)) Assert.AreEqual(HttpStatusCode.BadRequest, denied.StatusCode);
        using var initial = await admin.GetAsync("/settings/speed-tests", token);
        var scheduleFields = Hidden(await FormAsync(initial, "speed-settings", token));
        scheduleFields["Input.Hours"] = "6"; scheduleFields["Input.MaxMiB"] = "100"; scheduleFields["Input.Timeout"] = "45";
        scheduleFields["Input.Automatic"] = "false";
        using var scheduleSaved = await admin.PostAsync("/settings/speed-tests", new FormUrlEncodedContent(scheduleFields), token);
        Assert.Contains("設定已儲存", await TextAsync(scheduleSaved, token));
        var retentionForm = await FormAsync(scheduleSaved, "speed-retention", token);
        Assert.AreEqual("/settings/speed-tests#speed-retention-title", retentionForm.GetAttribute("action"));
        var fields = Hidden(retentionForm);
        Assert.AreEqual("speed-retention", fields["_handler"]);
        Assert.IsTrue(fields.ContainsKey("__RequestVerificationToken"));
        fields["Input.Days"] = "30";
        using var preview = await admin.PostAsync("/settings/speed-tests", new FormUrlEncodedContent(fields), token);
        Assert.Contains("預估有", await TextAsync(preview, token));
        var previewForm = await FormAsync(preview, "speed-retention", token);
        Assert.AreEqual("/settings/speed-tests?retention=cancel#speed-retention-title", previewForm.QuerySelector("a")?.GetAttribute("href"));
        using var cancelled = await admin.GetAsync("/settings/speed-tests?retention=cancel", token);
        var cancelledText = await TextAsync(cancelled, token);
        Assert.DoesNotContain("確認儲存", cancelledText);
        Assert.Contains("目前保存 365 天", cancelledText);
        var confirmedFields = Hidden(previewForm);
        confirmedFields["Input.Days"] = "30";
        using var confirmed = await admin.PostAsync("/settings/speed-tests", new FormUrlEncodedContent(confirmedFields), token);
        Assert.Contains("保存設定已儲存", await TextAsync(confirmed, token));
        var saved = await admin.GetFromJsonAsync<SpeedRetentionSnapshot>("/api/v1/settings/speed-retention", token);
        Assert.IsNotNull(saved);
        Assert.AreEqual(30, saved.Days);
        var nextSchedule = Hidden(await FormAsync(confirmed, "speed-settings", token));
        nextSchedule["Input.Hours"] = "12"; nextSchedule["Input.MaxMiB"] = "100"; nextSchedule["Input.Timeout"] = "45";
        nextSchedule["Input.Automatic"] = "false";
        using var scheduleAgain = await admin.PostAsync("/settings/speed-tests", new FormUrlEncodedContent(nextSchedule), token);
        Assert.Contains("設定已儲存", await TextAsync(scheduleAgain, token));
        using var stale = await admin.PostAsync("/settings/speed-tests", new FormUrlEncodedContent(confirmedFields), token);
        Assert.Contains("設定已被其他操作變更", await TextAsync(stale, token));
        var now = DateTimeOffset.UtcNow;
        var auditUrl = $"/api/v1/operations/audit?from={Uri.EscapeDataString(now.AddDays(-1).ToString("O"))}&to={Uri.EscapeDataString(now.AddDays(1).ToString("O"))}";
        var audit = await admin.GetFromJsonAsync<OperationAuditPage>(auditUrl, token);
        Assert.IsNotNull(audit);
        Assert.AreEqual(30, audit.Items.Single(x => x.SpeedRetention is not null).SpeedRetention?.CurrentDays);
    }, TestContext.CancellationToken);

    [TestMethod]
    public Task RetentionSave_ReportsSuccessWhenScheduleRefreshFails() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        var failure = new RefreshFailure();
        using var factory = PingTestSite.CreateFactory(database.ConnectionString).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            var options = new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(database.ConnectionString,
                sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "network")).AddInterceptors(failure).Options;
            services.RemoveAll<IDbContextFactory<NetworkDbContext>>();
            services.AddSingleton<IDbContextFactory<NetworkDbContext>>(new InterceptedFactory(options));
            services.RemoveAll<IOperationAuditWriter<OperationAudit>>();
            services.AddSingleton<IOperationAuditWriter<OperationAudit>>(new ArmFailureAfterAudit(failure));
        }));
        await PingTestSite.InitializeAsync(factory, token);
        using var admin = Client(factory); await LoginAsync(admin, "admin", token);
        using var initial = await admin.GetAsync("/settings/speed-tests", token);
        var fields = Hidden(await FormAsync(initial, "speed-retention", token));
        fields["Input.Days"] = "30";
        using var preview = await admin.PostAsync("/settings/speed-tests", new FormUrlEncodedContent(fields), token);
        var confirm = Hidden(await FormAsync(preview, "speed-retention", token));
        confirm["Input.Days"] = "30";
        using var result = await admin.PostAsync("/settings/speed-tests", new FormUrlEncodedContent(confirm), token);
        var document = await new HtmlParser().ParseDocumentAsync(await result.Content.ReadAsStringAsync(token), token);
        Assert.Contains("保存設定已儲存", document.Body!.TextContent);
        Assert.Contains("保存期限已儲存，但無法更新測速排程設定", document.Body.TextContent);
        Assert.IsFalse(document.QuerySelectorAll("form").Any(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "speed-settings"));
        await using var db = new NetworkDbContext(new DbContextOptionsBuilder<NetworkDbContext>().UseSqlServer(database.ConnectionString).Options);
        Assert.AreEqual(30, (await db.SpeedSettings.SingleAsync(token)).RetentionDays);
    }, TestContext.CancellationToken);

    [TestMethod]
    public Task Details_ShowCoordinatesOnlyWhenProvided() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token);
        var withCoordinates = new SpeedRun { RequestId = Guid.CreateVersion7(), RequestedAt = DateTimeOffset.UtcNow,
            State = SpeedTestState.Success, Latitude = 25.033, Longitude = 121.5654 };
        var missing = new SpeedRun { RequestId = Guid.CreateVersion7(), RequestedAt = DateTimeOffset.UtcNow, State = SpeedTestState.NoServer };
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>().CreateDbContextAsync(token))
        {
            db.SpeedRuns.AddRange(withCoordinates, missing);
            await db.SaveChangesAsync(token);
        }
        using var reader = Client(factory);
        await LoginAsync(reader, "reader", token);
        using var populatedPage = await reader.GetAsync($"/speed-tests/{withCoordinates.Id:D}", token);
        Assert.AreEqual(HttpStatusCode.OK, populatedPage.StatusCode);
        Assert.Contains("25.033, 121.5654", await TextAsync(populatedPage, token));
        using var missingPage = await reader.GetAsync($"/speed-tests/{missing.Id:D}", token);
        Assert.AreEqual(HttpStatusCode.OK, missingPage.StatusCode);
        var html = await new HtmlParser().ParseDocumentAsync(await missingPage.Content.ReadAsStringAsync(token), token);
        var coordinate = html.QuerySelectorAll("dl div").Single(x => x.QuerySelector("dt")?.TextContent == "伺服器座標");
        Assert.AreEqual("目前無資料", coordinate.QuerySelector("dd")?.TextContent);
        var isp = html.QuerySelectorAll("dl div").Single(x => x.QuerySelector("dt")?.TextContent == "測速端 ISP");
        Assert.AreEqual("目前無資料", isp.QuerySelector("dd")?.TextContent);
        Assert.Contains("此測試未完整完成", html.Body!.TextContent);
        using var notFoundPage = await reader.GetAsync($"/speed-tests/{Guid.CreateVersion7():D}", token);
        Assert.AreEqual(HttpStatusCode.OK, notFoundPage.StatusCode);
        Assert.Contains("找不到測速結果", await TextAsync(notFoundPage, token));
    }, TestContext.CancellationToken);
    private static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
    private static Dictionary<string,string> Hidden(IElement form) => form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
    private static async Task<IElement> FormAsync(HttpResponseMessage response,string handler,CancellationToken token) =>
        (await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token),token)).QuerySelectorAll("form").Single(x=>x.QuerySelector("input[name='_handler']")?.GetAttribute("value")==handler);
    private static async Task<string> TextAsync(HttpResponseMessage response,CancellationToken token) => (await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token),token)).Body!.TextContent;
    private static async Task LoginAsync(HttpClient client,string name,CancellationToken token)
    {
        using var page=await client.GetAsync("/account/login",token); var fields=Hidden(await FormAsync(page,"login",token));
        fields["Input.UserName"]=name;fields["Input.Password"]="Strong-Password-123";
        using var response=await client.PostAsync("/account/login",new FormUrlEncodedContent(fields),token);Assert.AreEqual(HttpStatusCode.Redirect,response.StatusCode);
    }

    private sealed class RefreshFailure : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("[network].[SpeedSettings]", StringComparison.Ordinal))
                throw new InvalidOperationException("Injected refresh failure");
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class InterceptedFactory(DbContextOptions<NetworkDbContext> options) : IDbContextFactory<NetworkDbContext>
    {
        public NetworkDbContext CreateDbContext() => new(options);
    }

    private sealed class ArmFailureAfterAudit(RefreshFailure failure) : IOperationAuditWriter<OperationAudit>
    {
        private readonly OperationAuditWriter inner = new();
        public async Task AppendAsync(DbConnection connection, DbTransaction transaction, OperationAudit audit, CancellationToken token)
        {
            await inner.AppendAsync(connection, transaction, audit, token);
            if (audit.Details is SpeedRetentionChanged) failure.Armed = true;
        }
    }
}
