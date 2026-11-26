using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vigiloom.Modules.AlertingOperations.Application.Line;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
[DoNotParallelize]
public sealed class LineSettingsBrowserTests(TestContext context)
{
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public Task AdministratorConfiguresGroupAndQueuesTest() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        factory.UseKestrel(0);
        factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        await using (var scope = factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<LineChannel>().RecordCandidatesAsync(["Ctest-group"], token);

        var url = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await page.GotoAsync(url + "/settings/line");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.GetByLabel("Channel access token").FillAsync("private-access-token");
        await page.GetByLabel("Channel secret").FillAsync("private-channel-secret");
        await page.Locator("#line-aggregation").FillAsync("25");
        await page.GetByLabel("啟用 LINE 通知").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "儲存設定", Exact = true }).ClickAsync();
        await page.GetByText("設定已儲存。").WaitForAsync();
        Assert.AreEqual("25", await page.Locator("#line-aggregation").InputValueAsync());
        Assert.AreEqual("", await page.GetByLabel("Channel access token").InputValueAsync());
        Assert.DoesNotContain("private-access-token", await page.Locator("body").InnerTextAsync());
        await page.GetByLabel("群組名稱").FillAsync("維運群組");
        await page.GetByRole(AriaRole.Button, new() { Name = "確認啟用", Exact = true }).ClickAsync();
        await page.GetByLabel("群組名稱").FillAsync("值班群組");
        await page.GetByRole(AriaRole.Button, new() { Name = "儲存名稱", Exact = true }).ClickAsync();
        await page.GetByText("值班群組", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "發送測試通知", Exact = true }).ClickAsync();
        await page.GetByText("測試通知已排入，等待背景工作處理。").WaitForAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "最近派送", Exact = true }).WaitForAsync();
        Assert.Contains("等待處理", await page.Locator("main").InnerTextAsync());
    }, context.CancellationToken);
}
