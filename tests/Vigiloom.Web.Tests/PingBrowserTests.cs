using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vigiloom.Contracts.Network;
using Vigiloom.Modules.NetworkMonitoring.Application;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
public sealed class PingBrowserTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task SettingsAndLatestResultsWorkAtDesktopAndMobileWidths() =>
        SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
        {
            using var factory = PingTestSite.CreateFactory(database.ConnectionString);
            factory.UseKestrel(0);
            factory.StartServer();
            var device = await PingTestSite.InitializeAsync(factory, token);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
                await db.Database.MigrateAsync(token);
            await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
                await db.Database.MigrateAsync(token);
            var url = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
            var page = await context.NewPageAsync();
            var errors = new List<string>();
            page.PageError += (_, error) => errors.Add(error);
            await page.GotoAsync(url + "/settings/ping");
            await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
            await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
            await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "Ping 監測", Exact = true }).WaitForAsync();
            await page.Locator("#ping-interval").FillAsync("10");
            await page.Locator("#ping-timeout").FillAsync("10");
            await page.GetByRole(AriaRole.Button, new() { Name = "儲存 Ping 設定" }).ClickAsync();
            await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "請檢查設定範圍" }).WaitForAsync();
            await page.Locator("#ping-interval").FillAsync("30");
            await page.Locator("#ping-timeout").FillAsync("2");
            await page.GetByRole(AriaRole.Button, new() { Name = "儲存 Ping 設定" }).ClickAsync();
            await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "設定已儲存" }).WaitForAsync();
            var evidence = Path.Combine(Environment.GetEnvironmentVariable("VIGILOOM_UI_EVIDENCE_DIR") ?? TestContext.TestRunDirectory!, "ping-ui");
            Directory.CreateDirectory(evidence);
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "settings-desktop.png"), FullPage = true });
            await page.SetViewportSizeAsync(390, 844);
            await page.ReloadAsync();
            Assert.IsTrue(await page.Locator("#ping-interval").IsVisibleAsync());
            Assert.IsTrue(await page.GetByRole(AriaRole.Button, new() { Name = "儲存 Ping 設定" }).IsVisibleAsync());
            Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "settings-mobile.png"), FullPage = true });
            await page.GetByRole(AriaRole.Link, new() { Name = "管理保存期限" }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "Ping 資料保存" }).WaitForAsync();
            Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "retention-mobile.png"), FullPage = true });
            await page.SetViewportSizeAsync(1440, 1000);
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "retention-desktop.png"), FullPage = true });
            await page.Locator("#ping-raw-days").FillAsync("20");
            await page.Locator("#ping-rollup-days").FillAsync("300");
            await page.GetByRole(AriaRole.Button, new() { Name = "預覽變更" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "確認儲存" }).WaitForAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "確認儲存" }).ClickAsync();
            await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "保存設定已儲存" }).WaitForAsync();
            await page.SetViewportSizeAsync(390, 844);
            await page.GotoAsync(url + "/settings/agents/metric-retention");
            await page.GetByRole(AriaRole.Heading, new() { Name = "主機效能資料保存" }).WaitForAsync();
            Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "host-metric-retention-mobile.png"), FullPage = true });
            await page.SetViewportSizeAsync(1440, 1000);
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "host-metric-retention-desktop.png"), FullPage = true });
            await page.Locator("#metric-raw-days").FillAsync("10");
            await page.Locator("#metric-rollup-days").FillAsync("60");
            await page.GetByRole(AriaRole.Button, new() { Name = "預覽變更" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "確認儲存" }).ClickAsync();
            await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "保存設定已儲存" }).WaitForAsync();
            await page.SetViewportSizeAsync(390, 844);
            await page.GotoAsync(url + "/settings/service-reception/retention");
            await page.GetByRole(AriaRole.Heading, new() { Name = "應用服務資料保存" }).WaitForAsync();
            Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "telemetry-retention-mobile.png"), FullPage = true });
            await page.SetViewportSizeAsync(1440, 1000);
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "telemetry-retention-desktop.png"), FullPage = true });
            await page.Locator("#telemetry-log-days").FillAsync("10");
            await page.Locator("#telemetry-trace-days").FillAsync("5");
            await page.GetByRole(AriaRole.Button, new() { Name = "預覽變更" }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "確認儲存" }).ClickAsync();
            await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "保存設定已儲存" }).WaitForAsync();
            await page.SetViewportSizeAsync(390, 844);
            await page.GotoAsync(url + "/settings/audit/retention");
            await page.GetByRole(AriaRole.Heading, new() { Name = "設定操作紀錄保存" }).WaitForAsync();
            Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "audit-retention-mobile.png"), FullPage = true });
            await page.SetViewportSizeAsync(1440, 1000);
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "audit-retention-desktop.png"), FullPage = true });
            await page.SetViewportSizeAsync(390, 844);
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var settings = scope.ServiceProvider.GetRequiredService<PingSettings>();
                var store = scope.ServiceProvider.GetRequiredService<PingStore>();
                await store.ReconcileAsync(await settings.GetAsync(token), token);
                var network = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NetworkDbContext>>();
                for (var round = 0; round < 2; round++)
                {
                    await using var db = await network.CreateDbContextAsync(token);
                    await db.Database.ExecuteSqlRawAsync("UPDATE network.PingTargets SET DueAt = DATEADD(second,-1,TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00'))", token);
                    await new PingRunner(settings, store, new SystemPingProbe(), TimeProvider.System).RunOnceAsync(token);
                }
            }
            await page.GotoAsync(url + $"/devices/{device.Id:D}");
            Assert.Contains("可達", await page.Locator("main").InnerTextAsync());
            Assert.Contains("成功", await page.Locator("tbody").InnerTextAsync());
            Assert.Contains("127.0.0.1", await page.Locator("tbody").InnerTextAsync());
            Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "device-mobile.png"), FullPage = true });
            await page.SetViewportSizeAsync(1440, 1000);
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "device-desktop.png"), FullPage = true });
            Assert.IsEmpty(errors);
        }, TestContext.CancellationToken);
}
