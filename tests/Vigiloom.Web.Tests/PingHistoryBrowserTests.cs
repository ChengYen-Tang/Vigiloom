using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vigiloom.Contracts.Network;
using Vigiloom.Modules.NetworkMonitoring.Application.PingHistory;
using Vigiloom.Modules.NetworkMonitoring.Domain;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
[DoNotParallelize]
public sealed class PingHistoryBrowserTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Reader_SeesHistoryAndCanFilterDates() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        factory.UseKestrel(0); factory.StartServer();
        var device = await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        var longDevice = new Device(new string('W', 256), "127.0.0.2", false, DateTimeOffset.UtcNow);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        { db.Devices.Add(longDevice); await db.SaveChangesAsync(token); }
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>();
        var day = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        await using (var db = await contexts.CreateDbContextAsync(token))
        {
            db.PingSamples.AddRange(
                new PingSample(device.Id, device.Address, "127.0.0.1", "central", 1, day.AddHours(1), day.AddHours(1), PingOutcome.Success, 12),
                new PingSample(device.Id, device.Address, "127.0.0.1", "central", 2, day.AddHours(1).AddMinutes(1), day.AddHours(1).AddMinutes(1), PingOutcome.Timeout, null),
                new PingSample(device.Id, device.Address, "127.0.0.1", "central", 3, day.AddHours(1).AddMinutes(10), day.AddHours(1).AddMinutes(10), PingOutcome.Success, 24),
                new PingSample(device.Id, "example.net", "127.0.0.1", "central", 1, day.AddHours(1), day.AddHours(1), PingOutcome.Success, 40));
            await db.SaveChangesAsync(token);
        }
        Assert.AreEqual(4, await new PingRollupBackfill(contexts).RunBatchAsync(token));
        var url = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var browserContext = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1486, Height = 1058 } });
        var page = await browserContext.NewPageAsync();
        var browserErrors = new List<string>();
        page.PageError += (_, message) => browserErrors.Add(message);
        page.Console += (_, message) => { if (message.Type == "error") browserErrors.Add(message.Text); };
        await using var wasmGate = await AutoRenderGate.HoldAsync(page);
        await page.GotoAsync($"{url}/devices/{device.Id:D}");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("reader");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "查看 Ping 歷史" }).ClickAsync();
        await page.Locator(".ping-history[data-runtime='Server'][data-interactive='true']").WaitForAsync(new() { Timeout = 60000 });
        try { await page.GetByRole(AriaRole.Cell, new() { Name = "12 ms", Exact = true }).WaitForAsync(new() { Timeout = 10000 }); }
        catch (TimeoutException) { Assert.Fail(await page.Locator("body").InnerTextAsync()); }
        await page.GetByText("逾時", new() { Exact = true }).WaitForAsync();
        await page.GetByText("18 ms", new() { Exact = true }).WaitForAsync();
        await page.GetByText("只平均成功的量測", new() { Exact = false }).WaitForAsync();
        await page.GetByLabel("搜尋目標或監測位置").FillAsync("does-not-match");
        await page.GetByRole(AriaRole.Option, new() { Name = "example.net · central" }).WaitForAsync(new() { State = WaitForSelectorState.Detached });
        Assert.AreEqual(0, await page.GetByRole(AriaRole.Option, new() { Name = "example.net · central" }).CountAsync());
        await page.GetByLabel("搜尋目標或監測位置").FillAsync("");
        await page.GetByRole(AriaRole.Option, new() { Name = "example.net · central" }).WaitForAsync(new() { State = WaitForSelectorState.Attached });
        Assert.AreEqual(1, await page.GetByRole(AriaRole.Option, new() { Name = "example.net · central" }).CountAsync());
        try { await page.Locator(".time-series-chart svg").WaitForAsync(new() { Timeout = 10000 }); }
        catch (TimeoutException) { Assert.Fail("Chart did not render. " + string.Join("\n", browserErrors)); }
        await page.GetByRole(AriaRole.Heading, new() { Name = "五分鐘平均延遲趨勢" }).ClickAsync();
        if (Environment.GetEnvironmentVariable("VIGILOOM_PING_UI_EVIDENCE_DIR") is { Length: > 0 } evidence)
        {
            Directory.CreateDirectory(evidence);
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "desktop.png"), FullPage = true });
            await page.SetViewportSizeAsync(390, 844);
            await page.WaitForFunctionAsync("() => document.documentElement.scrollWidth <= window.innerWidth && document.body.scrollWidth <= window.innerWidth && document.querySelector('.time-series-chart svg')?.getBoundingClientRect().width <= window.innerWidth");
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "mobile.png"), FullPage = true });
        }
        await page.SetViewportSizeAsync(390, 844);
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth"));
        await page.GetByLabel("時間排序").SelectOptionAsync("asc");
        await page.GetByRole(AriaRole.Button, new() { Name = "查詢", Exact = true }).ClickAsync();
        await page.Locator(".ping-history .device-table-wrap tbody tr").First.GetByRole(AriaRole.Cell, new() { Name = "12 ms", Exact = true }).WaitForAsync();
        await page.GetByLabel("開始日期（UTC）").FillAsync("2030-01-02");
        await page.GetByLabel("結束日期（UTC）").FillAsync("2030-01-01");
        await page.GetByRole(AriaRole.Button, new() { Name = "查詢", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Alert).GetByText("開始日期不可晚於結束日期", new() { Exact = false }).WaitForAsync();
        await page.GetByLabel("開始日期（UTC）").FillAsync("2030-01-01");
        await page.GetByRole(AriaRole.Button, new() { Name = "查詢", Exact = true }).ClickAsync();
        await page.GetByText("此範圍沒有已彙總的 Ping 資料。", new() { Exact = true }).WaitForAsync();
        await page.GetByText("此頁沒有原始量測紀錄", new() { Exact = false }).WaitForAsync();
        await wasmGate.ReleaseAsync();
        await AutoRenderGate.WaitForWebAssemblyCachedAsync(page);
        await page.ReloadAsync();
        await page.Locator(".ping-history[data-runtime='WebAssembly'][data-interactive='true']").WaitForAsync(new() { Timeout = 60000 });
        await page.GetByRole(AriaRole.Cell, new() { Name = "12 ms", Exact = true }).WaitForAsync(new() { Timeout = 30000 });
        await page.GotoAsync($"{url}/devices/{longDevice.Id:D}/ping-history");
        await page.GetByText(new string('W', 256), new() { Exact = true }).WaitForAsync();
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth"));
    }, context.CancellationToken);
}
