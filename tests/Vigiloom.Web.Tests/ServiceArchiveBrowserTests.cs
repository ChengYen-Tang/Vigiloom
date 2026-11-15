using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
[DoNotParallelize]
public sealed class ServiceArchiveBrowserTests(TestContext testContext)
{
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public Task ArchiveAndRestore_AreUsableOnDesktopAndMobile() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        factory.UseKestrel(0);
        factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token);
        var now = DateTimeOffset.UtcNow;
        var source = new ServiceSource("source", new byte[32], now);
        var service = new MonitoredService(new ServiceCandidate(source.Id, "orders", "prod", "Orders", now), now);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
        {
            await db.Database.MigrateAsync(token);
            db.Sources.Add(source); db.Services.Add(service); await db.SaveChangesAsync(token);
        }
        var root = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var browserContext = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
        var page = await browserContext.NewPageAsync();
        await page.GotoAsync(root + "/settings/service-instances");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "服務管理", Exact = true }).WaitForAsync();
        var card = page.Locator("li").Filter(new() { HasText = "Orders（prod）" });
        await card.GetByText("封存服務", new() { Exact = true }).ClickAsync();
        var evidence = Environment.GetEnvironmentVariable("VIGILOOM_SERVICE_ARCHIVE_EVIDENCE_DIR") ?? Path.Combine(testContext.TestRunDirectory!, "service-archive-ui");
        Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "desktop.png"), FullPage = true });
        await page.SetViewportSizeAsync(390, 844);
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "mobile.png"), FullPage = true });
        await card.GetByRole(AriaRole.Checkbox, new() { Name = "我已確認影響" }).Last.CheckAsync();
        await card.GetByRole(AriaRole.Button, new() { Name = "確認封存", Exact = true }).ClickAsync();
        await page.GetByText("服務已封存並撤回公開。", new() { Exact = true }).WaitForAsync();
        await card.GetByText("恢復服務", new() { Exact = true }).ClickAsync();
        await card.GetByRole(AriaRole.Checkbox, new() { Name = "我已確認影響" }).CheckAsync();
        await card.GetByRole(AriaRole.Button, new() { Name = "確認恢復服務", Exact = true }).ClickAsync();
        await page.GetByText("服務已恢復，維持私人及停止收集。", new() { Exact = true }).WaitForAsync();
        testContext.AddResultFile(Path.Combine(evidence, "desktop.png"));
        testContext.AddResultFile(Path.Combine(evidence, "mobile.png"));
    }, testContext.CancellationToken);
}
