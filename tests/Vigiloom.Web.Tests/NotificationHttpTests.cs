using System.Net;
using AngleSharp.Html.Parser;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vigiloom.Modules.AlertingOperations.Domain;
using Vigiloom.Modules.AlertingOperations.Persistence;
using Vigiloom.Web.Tests.Fixtures;
using Vigiloom.Testing.SqlServer;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class NotificationHttpTests(TestContext context)
{
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public Task AlertEpisodes_AreReadableOnDesktopAndMobile() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        factory.UseKestrel(0);
        factory.StartServer();
        var device = await PingTestSite.InitializeAsync(factory, token);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token))
        {
            db.Set<PingAlert>().Add(new PingAlert { DeviceId = device.Id, Target = "192.0.2.1", OpenedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(token);
        }
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var browserContext = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1024 } });
        var page = await browserContext.NewPageAsync();
        await page.GotoAsync(factory.ClientOptions.BaseAddress.ToString().TrimEnd('/') + "/notifications");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("reader");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Ping 與服務告警生命週期" }).WaitForAsync();
        await page.GetByText("192.0.2.1", new() { Exact = true }).WaitForAsync();
        var evidence = Environment.GetEnvironmentVariable("VIGILOOM_ALERT_EVIDENCE_DIR");
        if (evidence is not null) { Directory.CreateDirectory(evidence); await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "desktop.png"), FullPage = true }); }
        await page.SetViewportSizeAsync(390, 844);
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        if (evidence is not null) await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "mobile.png"), FullPage = true });
    }, context.CancellationToken);

    [TestMethod]
    public Task NotificationPage_RequiresLoginAndFiltersRealRecords() =>
        ReusableWebDatabase.ExecuteAsync(async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            var device = await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
            var now = DateTimeOffset.UtcNow;
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<OperationsDbContext>>().CreateDbContextAsync(token))
            {
                db.Set<Notification>().AddRange(
                    new Notification { MessageId = Guid.CreateVersion7(), Source = "ping", OccurredAt = now,
                        Title = "Ping 無法連線", Detail = "<script>alert(1)</script>", ResourceId = device.Id },
                    new Notification { MessageId = Guid.CreateVersion7(), Source = "sigma", OccurredAt = now,
                        Title = "行為規則命中", Detail = "需要調查", ResourceId = device.Id });
                db.Set<PingAlert>().Add(new PingAlert { DeviceId = device.Id, Target = "192.0.2.1", OpenedAt = now });
                await db.SaveChangesAsync(token);
            }
            using var anonymous = PingTestSite.Client(factory);
            using var denied = await anonymous.GetAsync("/notifications", token);
            Assert.AreEqual(HttpStatusCode.Redirect, denied.StatusCode);
            using var reader = PingTestSite.Client(factory);
            await PingTestSite.LoginAsync(reader, "reader", token);
            using var page = await reader.GetAsync("/notifications?source=ping", token);
            Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
            var html = await page.Content.ReadAsStringAsync(token);
            var document = await new HtmlParser().ParseDocumentAsync(html, token);
            Assert.Contains("Ping 無法連線", document.Body!.TextContent);
            Assert.DoesNotContain("行為規則命中", document.Body.TextContent);
            Assert.Contains("此處的紀錄不代表通知已送達", document.Body.TextContent);
            Assert.Contains("192.0.2.1", document.Body.TextContent);
            Assert.Contains("尚未恢復", document.Body.TextContent);
            Assert.Contains("不重複建立 Ping 不可達通知", document.Body.TextContent);
            Assert.IsNull(document.QuerySelector(".notifications-table script"));
            Assert.IsNotNull(document.QuerySelector($"a[href='/devices/{device.Id:D}']"));
            using var invalid = await reader.GetAsync("/notifications?from=2026-11-03&to=2026-11-01", token);
            Assert.Contains("開始日期不能晚於結束日期", (await new HtmlParser().ParseDocumentAsync(await invalid.Content.ReadAsStringAsync(token), token)).Body!.TextContent);
            using var empty = await reader.GetAsync("/notifications?source=vulnerability", token);
            Assert.Contains("沒有符合條件的通知紀錄", (await new HtmlParser().ParseDocumentAsync(await empty.Content.ReadAsStringAsync(token), token)).Body!.TextContent);
        }, context.CancellationToken);
}
