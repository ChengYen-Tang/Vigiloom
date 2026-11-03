using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
[DoNotParallelize]
public sealed class AgentCapacityBrowserTests(TestContext testContext)
{
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public Task CapacitySettings_DesktopAndMobileRemainUsable() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        factory.UseKestrel(0);
        factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        var url = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
        var page = await context.NewPageAsync();
        await page.GotoAsync(url + "/settings/agents");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "本機佇列容量", Exact = true }).WaitForAsync();
        Assert.Contains("降低上限不會刪除已保留資料", await page.Locator("main").InnerTextAsync());
        var evidence = Environment.GetEnvironmentVariable("VIGILOOM_CAPACITY_EVIDENCE_DIR") ?? Path.Combine(testContext.TestRunDirectory!, "agent-capacity-ui");
        Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "desktop.png"), FullPage = true });
        await page.SetViewportSizeAsync(390, 844);
        await page.ReloadAsync();
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        await page.GetByLabel("批次內容容量（1～1024 MiB）").FillAsync("128");
        await page.GetByLabel("批次數量上限（1～100000 批）").FillAsync("200");
        await page.GetByRole(AriaRole.Button, new() { Name = "儲存佇列容量", Exact = true }).ClickAsync();
        Assert.Contains("目前目標值：128 MiB", await page.Locator("main").InnerTextAsync());
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "mobile.png"), FullPage = true });
        testContext.AddResultFile(Path.Combine(evidence, "desktop.png"));
        testContext.AddResultFile(Path.Combine(evidence, "mobile.png"));
    }, testContext.CancellationToken);
}
