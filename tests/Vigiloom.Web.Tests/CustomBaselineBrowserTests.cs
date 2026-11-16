using Microsoft.Playwright;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
[DoNotParallelize]
public sealed class CustomBaselineBrowserTests(TestContext context)
{
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public Task Administrator_CreatesReviewsAndActivatesDraft() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        factory.UseKestrel(0);
        factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var browserContext = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1536, Height = 1024 } });
        var page = await browserContext.NewPageAsync();
        await page.GotoAsync(factory.ClientOptions.BaseAddress.ToString().TrimEnd('/') + "/settings/baseline-checks");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.GetByLabel("檢查名稱").FillAsync("SSH 權限檢查");
        await page.GetByLabel("適用作業系統").SelectOptionAsync("ubuntu-both");
        await page.GetByLabel("檢查目標").FillAsync("/etc/ssh/sshd_config");
        await page.GetByLabel("預期值").FillAsync("0600");
        await page.GetByLabel("處理建議（選填）").FillAsync("請確認檔案權限。");
        await page.SetViewportSizeAsync(390, 844);
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        await page.SetViewportSizeAsync(1536, 1024);
        await page.GetByRole(AriaRole.Button, new() { Name = "儲存草稿", Exact = true }).ClickAsync();
        var approve = page.GetByRole(AriaRole.Button, new() { Name = "審核並啟用", Exact = true });
        await approve.WaitForAsync();
        Assert.AreEqual("ubuntu-both", await page.GetByLabel("適用作業系統").InputValueAsync());
        Assert.Contains("/etc/ssh/sshd_config", await page.Locator("aside").InnerTextAsync());
        Assert.Contains("0600", await page.Locator("aside").InnerTextAsync());
        await page.GetByLabel("預期值").FillAsync("0640");
        await page.GetByRole(AriaRole.Button, new() { Name = "儲存草稿", Exact = true }).ClickAsync();
        await approve.WaitForAsync();
        Assert.Contains("0640", await page.Locator("aside").InnerTextAsync());
        Assert.AreEqual("ubuntu-both", await page.GetByLabel("適用作業系統").InputValueAsync());
        Assert.Contains("Ubuntu 22.04、Ubuntu 24.04", await page.Locator("aside").InnerTextAsync());
        await approve.ClickAsync();
        Assert.Contains("版本已啟用", await page.Locator("main").InnerTextAsync());
    }, context.CancellationToken);
}
