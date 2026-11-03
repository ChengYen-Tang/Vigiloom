using Microsoft.EntityFrameworkCore;
using Hangfire;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.AlertingOperations.Application;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
[DoNotParallelize]
public sealed class PlatformHealthBrowserTests(TestContext testContext)
{
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public Task HealthPage_LayoutSmokeOnDesktopAndMobile() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        factory.UseKestrel(0);
        factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<SchedulingDbContext>>().CreateDbContextAsync(token)) await db.Database.MigrateAsync(token);
        GlobalConfiguration.Configuration.UseNoOpLogProvider();
        await HangfireSchemaInstaller.InstallAsync(database.ConnectionString, token);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<WorkerDiagnosticsStore>();
            var boot = Guid.CreateVersion7();
            var slot = await store.ClaimAsync(boot, token);
            Assert.IsNotNull(slot);
            var now = DateTimeOffset.UtcNow;
            Assert.IsTrue(await store.UpdateAsync(slot.Value, boot,
                new WorkerObservation(new(now, now, 2, ScannerFailureKind.Timeout), new(now, null, 0)), token));
        }
        var url = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(url + "/settings/jobs");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "平台健康", Exact = true }).WaitForAsync();
        Assert.Contains("保留範圍內沒有成功批次紀錄", await page.Locator("main").InnerTextAsync());
        Assert.Contains("掃描逾時", await page.Locator("main").InnerTextAsync());
        await Assertions.Expect(page.GetByText("← 左右滑動查看完整表格 →")).ToBeHiddenAsync();
        var evidence = Environment.GetEnvironmentVariable("VIGILOOM_HEALTH_EVIDENCE_DIR")
            ?? Path.Combine(testContext.TestRunDirectory!, "platform-health-ui");
        Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "desktop.png"), FullPage = true });
        testContext.AddResultFile(Path.Combine(evidence, "desktop.png"));
        await page.SetViewportSizeAsync(390, 844);
        await page.ReloadAsync();
        await Assertions.Expect(page.GetByText("← 左右滑動查看完整表格 →")).ToBeVisibleAsync();
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        var table = page.GetByRole(AriaRole.Region, new() { Name = "Worker 掃描摘要，可左右捲動" });
        await table.FocusAsync();
        await page.Keyboard.PressAsync("ArrowRight");
        await page.WaitForFunctionAsync("() => document.querySelector('[aria-label=\"Worker 掃描摘要，可左右捲動\"]')?.scrollLeft > 0");
        Assert.IsGreaterThan(0, await table.EvaluateAsync<double>("element => element.scrollLeft"));
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "mobile.png"), FullPage = true });
        testContext.AddResultFile(Path.Combine(evidence, "mobile.png"));
        Assert.IsEmpty(errors);
    }, testContext.CancellationToken);
}
