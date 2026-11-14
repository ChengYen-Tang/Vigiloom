using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
[DoNotParallelize]
public sealed class ProcessPolicyBrowserTests(TestContext context)
{
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public Task ProcessPolicy_DesktopAndMobileSupportSafeOverride() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        factory.UseKestrel(0);
        factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        var url = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var session = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
        var page = await session.NewPageAsync();
        await page.GotoAsync(url + "/settings/agents");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        var input = page.GetByLabel("程序執行資訊", new() { Exact = true });
        await input.WaitForAsync();
        var section = page.Locator("section").Filter(new() { Has = page.GetByRole(AriaRole.Heading, new() { Name = "主機盤點", Exact = true }) });
        Assert.Contains("參數不會傳送到平台", await section.InnerTextAsync());
        var evidence = Environment.GetEnvironmentVariable("VIGILOOM_REQ28_EVIDENCE_DIR") ?? Path.Combine(context.TestRunDirectory!, "req28-policy");
        Directory.CreateDirectory(evidence);
        await section.ScreenshotAsync(new() { Path = Path.Combine(evidence, "desktop.png") });
        await page.SetViewportSizeAsync(390, 844);
        await page.ReloadAsync();
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        await input.SelectOptionAsync("true");
        await section.GetByRole(AriaRole.Button, new() { Name = "儲存盤點設定", Exact = true }).ClickAsync();
        Assert.Contains("目前目標值：收集路徑，所有參數遮罩", await section.InnerTextAsync());
        Assert.AreEqual("true", await input.InputValueAsync());
        await section.ScreenshotAsync(new() { Path = Path.Combine(evidence, "mobile.png") });
        context.AddResultFile(Path.Combine(evidence, "desktop.png"));
        context.AddResultFile(Path.Combine(evidence, "mobile.png"));
    }, context.CancellationToken);
}
