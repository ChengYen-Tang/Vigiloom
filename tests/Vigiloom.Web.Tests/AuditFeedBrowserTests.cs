using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.IdentityAccess.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
[DoNotParallelize]
public sealed class AuditFeedBrowserTests(TestContext testContext)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task AuditSources_KeepSelectionAndWorkOnMobile() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        factory.UseKestrel(0);
        factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token);
        var account = Guid.CreateVersion7();
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<IdentityAccessDbContext>>().CreateDbContextAsync(token))
        {
            db.AccountAuditEvents.Add(new AccountAuditEvent(null, account, "PasswordResetCompleted", DateTimeOffset.UtcNow));
            db.AccountAuditEvents.Add(new AccountAuditEvent(null, account, "unapproved-sensitive-action", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(token);
        }
        var url = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(url + "/settings/audit");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "操作稽核", Exact = true }).WaitForAsync();
        await page.GetByLabel("紀錄來源").SelectOptionAsync("accounts");
        await page.GetByRole(AriaRole.Button, new() { Name = "查詢", Exact = true }).ClickAsync();
        Assert.AreEqual("accounts", await page.GetByLabel("紀錄來源").InputValueAsync());
        var text = await page.Locator("main").InnerTextAsync();
        Assert.Contains("完成密碼重設", text);
        Assert.Contains("系統／機器流程", text);
        Assert.Contains("未支援的紀錄", text);
        Assert.DoesNotContain("unapproved-sensitive-action", text);
        var evidence = Environment.GetEnvironmentVariable("VIGILOOM_AUDIT_EVIDENCE_DIR")
            ?? Path.Combine(testContext.TestRunDirectory!, "audit-feed-ui");
        Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "desktop.png"), FullPage = true });
        await page.SetViewportSizeAsync(390, 844);
        await page.ReloadAsync();
        Assert.AreEqual("accounts", await page.GetByLabel("紀錄來源").InputValueAsync());
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        var table = page.GetByRole(AriaRole.Region, new() { Name = "稽核紀錄表，可左右捲動" });
        await table.FocusAsync();
        await page.Keyboard.PressAsync("ArrowRight");
        await page.WaitForFunctionAsync("() => document.querySelector('[aria-label=\"稽核紀錄表，可左右捲動\"]')?.scrollLeft > 0");
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "mobile.png"), FullPage = true });
        Assert.IsEmpty(errors);
    }, testContext.CancellationToken);
}
