using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vigiloom.Contracts.Intelligence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence;
using Vigiloom.Modules.SecurityAnalysis.Application.Intelligence.Baselines;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
[DoNotParallelize]
public sealed class BaselineExceptionBrowserTests(TestContext context)
{
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public Task AdministratorRecordsExplanation_ReaderCanSeeButCannotAdd() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        factory.UseKestrel(0);
        factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        var device = new Device("exception host", "127.0.0.1", true, DateTimeOffset.UtcNow);
        await using (var resources = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        { resources.Devices.Add(device); await resources.SaveChangesAsync(token); }
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var actor = Guid.CreateVersion7();
            var check = new BaselineCheck("draft", "SSH access", "linux", ["ubuntu:24.04"], BaselineSources.LinuxShadowOperation,
                "/etc/ssh/sshd_config", "0600", "", "管理員自訂");
            var staged = await scope.ServiceProvider.GetRequiredService<CustomBaselineStage>().SaveAsync(null, null, check, actor, token);
            Assert.AreEqual(CustomBaselineStageStatus.Staged, staged.Status);
            var published = await scope.ServiceProvider.GetRequiredService<IntelligencePublication>().ActivateAsync(staged.SourceId!.Value,
                staged.RevisionId!.Value, null, "", false, actor, token);
            Assert.AreEqual(IntelligencePublicationResult.Published, published);
        }
        var url = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        async Task<IPage> Login(IBrowserContext browserContext, string account)
        {
            var page = await browserContext.NewPageAsync();
            await page.GotoAsync(url + $"/devices/{device.Id:D}");
            await page.GetByLabel("帳號", new() { Exact = true }).FillAsync(account);
            await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
            await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
            return page;
        }
        await using (var adminContext = await browser.NewContextAsync())
        {
            var page = await Login(adminContext, "admin");
            await page.GetByRole(AriaRole.Link, new() { Name = "新增例外說明", Exact = true }).ClickAsync();
            await page.GetByLabel("例外原因").FillAsync("維護期間的暫時設定。");
            await page.GetByRole(AriaRole.Button, new() { Name = "儲存說明", Exact = true }).ClickAsync();
            await page.GetByText("最近的例外說明（1）").ClickAsync();
            Assert.Contains("維護期間的暫時設定。", await page.Locator("main").InnerTextAsync());
        }
        await using (var readerContext = await browser.NewContextAsync())
        {
            var page = await Login(readerContext, "reader");
            Assert.AreEqual(0, await page.GetByRole(AriaRole.Link, new() { Name = "新增例外說明", Exact = true }).CountAsync());
            await page.GetByText("最近的例外說明（1）").ClickAsync();
            Assert.Contains("維護期間的暫時設定。", await page.Locator("main").InnerTextAsync());
        }
    }, context.CancellationToken);
}
