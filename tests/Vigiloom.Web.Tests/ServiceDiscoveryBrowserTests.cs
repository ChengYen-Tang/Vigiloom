using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
[DoNotParallelize]
public sealed class ServiceDiscoveryBrowserTests(TestContext context)
{
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public Task CandidatePage_DesktopAndMobile() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        factory.UseKestrel(0);
        factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        await using var scope = factory.Services.CreateAsyncScope();
        var names = new[] { ("demo-collector", "訂單處理服務"), ("dc-west", "商品目錄 API"), ("test-agent-01", "會員驗證服務"), ("dc-analytics", "報表處理服務"), ("dev-collector", "通知轉送器") };
        foreach (var (name, service) in names)
        {
            var source = await scope.ServiceProvider.GetRequiredService<SourceAdministration>().IssueAsync(name, Guid.CreateVersion7(), token);
            await scope.ServiceProvider.GetRequiredService<CandidateDiscovery>().ObserveAsync(source.SourceId, service, "正式", service, token);
        }
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var browserContext = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1024 } });
        var page = await browserContext.NewPageAsync();
        await page.GotoAsync(factory.ClientOptions.BaseAddress.ToString().TrimEnd('/') + "/settings/service-discovery");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "待加入服務", Exact = true }).WaitForAsync();
        Assert.Contains("訂單處理服務", await page.Locator("main").InnerTextAsync());
        var evidence = Environment.GetEnvironmentVariable("VIGILOOM_SERVICE_EVIDENCE_DIR") ?? Path.Combine(context.TestRunDirectory!, "service-discovery-ui");
        Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "desktop.png"), FullPage = true });
        await page.SetViewportSizeAsync(390, 844);
        await page.ReloadAsync();
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "mobile.png"), FullPage = true });
        var expand = page.Locator("summary").Filter(new() { HasTextString = "加入服務" }).First;
        await expand.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        var confirmation = page.GetByLabel("我已確認來源及環境，並了解服務預設為私人").First;
        await confirmation.FocusAsync();
        await page.Keyboard.PressAsync("Space");
        Assert.IsTrue(await confirmation.IsCheckedAsync());
        await page.Keyboard.PressAsync("Tab");
        Assert.IsTrue(await page.GetByRole(AriaRole.Button, new() { Name = "加入服務", Exact = true }).First.EvaluateAsync<bool>("element => document.activeElement === element"));
        await page.Keyboard.PressAsync("Enter");
        await page.GetByText("服務已加入，預設為私人；正式遙測接收尚未完成。", new() { Exact = false }).WaitForAsync();
        context.AddResultFile(Path.Combine(evidence, "desktop.png"));
        context.AddResultFile(Path.Combine(evidence, "mobile.png"));
    }, context.CancellationToken);
}
