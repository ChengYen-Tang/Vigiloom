using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Modules.IdentityAccess.Domain;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Domain.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;
[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
public sealed class SpeedBrowserTests
{
    public TestContext TestContext { get; set; } = null!;
    [TestMethod]
    public Task HistoryUsesServerThenWebAssemblyAndKeepsFailedPointsEmpty() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString); factory.UseKestrel(0); factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>().CreateDbContextAsync(token))
        {
            var now = DateTimeOffset.UtcNow.AddMinutes(-5);
            db.SpeedRuns.AddRange(new SpeedRun { RequestId = Guid.CreateVersion7(), RequestedAt = now, State = SpeedTestState.Success,
                Server = "speedtest.example.com", DownloadMbps = 80, UploadMbps = 20, LatencyMilliseconds = 4, Bytes = 1024, CompletedAt = now },
                new SpeedRun { RequestId = Guid.CreateVersion7(), RequestedAt = now.AddMinutes(1), State = SpeedTestState.BudgetExceeded,
                    Server = "speedtest.example.com", DownloadMbps = 40, Bytes = 1048576, CompletedAt = now.AddMinutes(1) },
                new SpeedRun { RequestId = Guid.CreateVersion7(), RequestedAt = now.AddMinutes(2), State = SpeedTestState.NoServer, CompletedAt = now.AddMinutes(2) });
            await db.SaveChangesAsync(token);
        }
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
        var page = await context.NewPageAsync(); var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        await using var wasmGate = await AutoRenderGate.HoldAsync(page);
        page.PageError += (_, value) => errors.Enqueue(value);
        var browserHistoryRequests = 0;
        page.Request += (_, request) => { if (request.Url.Contains("/api/v1/speed-tests?", StringComparison.Ordinal)) Interlocked.Increment(ref browserHistoryRequests); };
        var url = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        await page.GotoAsync(url + "/speed-tests");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("reader");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.Locator("main[data-runtime='Server']").WaitForAsync(new() { Timeout = 60000 });
        await wasmGate.ReleaseAsync();
        Assert.DoesNotContain("日期範圍須", await page.Locator("main").InnerTextAsync());
        await page.Locator(".device-table-wrap tbody tr").First.WaitForAsync();
        Assert.AreEqual(3, await page.Locator(".device-table-wrap tbody tr").CountAsync());
        Assert.AreEqual(0, await page.GetByRole(AriaRole.Link, new() { Name = "手動測速", Exact = true }).CountAsync());
        await AssertChartAsync(page);
        var returnHref = await page.Locator(".device-table-wrap a").First.GetAttributeAsync("href");
        Assert.Contains("returnUrl=%2Fspeed-tests%3Ffrom%3D", returnHref!);
        await page.WaitForFunctionAsync("() => performance.getEntriesByType('resource').some(x => x.name.includes('/_framework/Vigiloom.Web.Client.') && x.name.includes('.wasm') && x.responseEnd > 0)", null, new() { Timeout = 90000 });
        await AutoRenderGate.WaitForWebAssemblyCachedAsync(page);
        await page.ReloadAsync();
        await page.Locator("main[data-runtime='WebAssembly']").WaitForAsync(new() { Timeout = 60000 });
        await AssertChartAsync(page);
        Assert.AreEqual(0, browserHistoryRequests, "Prerendered history must survive activation without a duplicate browser query.");
        await page.SetViewportSizeAsync(390, 844);
        await page.WaitForFunctionAsync("() => { const e = document.querySelector('.time-series-chart [role=img] > div'); const c = window.echarts?.getInstanceByDom(e); return c && c.getWidth() <= innerWidth && Math.abs(c.getWidth() - e.clientWidth) < 3; }", null, new() { Timeout = 10000 });
        var overflows = await page.EvaluateAsync<string>("() => JSON.stringify([...document.querySelectorAll('body *')].filter(e => e.getBoundingClientRect().right > innerWidth + 2 && getComputedStyle(e).position !== 'absolute').slice(0,10).map(e => [e.tagName, e.className, Math.round(e.getBoundingClientRect().right)]))");
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"), overflows);
        await page.Locator("#speed-search").FillAsync("not-a-server");
        await page.GetByRole(AriaRole.Button, new() { Name = "套用篩選" }).ClickAsync();
        await page.GetByText("這段時間沒有符合條件的測速紀錄。", new() { Exact = false }).WaitForAsync();
        await page.Locator("#speed-search").FillAsync("speedtest");
        await page.Locator("#speed-sort").SelectOptionAsync("download");
        await page.GetByRole(AriaRole.Button, new() { Name = "套用篩選" }).ClickAsync();
        await page.WaitForFunctionAsync("() => { const r = document.querySelectorAll('.device-table-wrap tbody tr'); return r.length === 2 && r[0].innerText.includes('80') && r[1].innerText.includes('40'); }");
        await page.Locator("#speed-search").FillAsync("");
        await page.Locator("#speed-sort").SelectOptionAsync("time");
        await page.GetByRole(AriaRole.Button, new() { Name = "套用篩選" }).ClickAsync();
        await page.WaitForFunctionAsync("() => { const r = document.querySelectorAll('.device-table-wrap tbody tr'); return r.length === 3 && r[0].innerText.includes('未選定'); }");
        await page.Locator(".device-table-wrap a").First.ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "測速結果", Exact = true }).WaitForAsync();
        Assert.Contains("未完整完成", await page.Locator("main").InnerTextAsync());
        Assert.Contains("目前無資料", await page.Locator("main").InnerTextAsync());
        Assert.AreEqual(0, await page.GetByRole(AriaRole.Button, new() { Name = "取消這筆測試" }).CountAsync());
        await page.GotoAsync(url + "/speed-tests");
        await page.Locator("main[data-runtime='WebAssembly']").WaitForAsync(new() { Timeout = 60000 });
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>(); var reader = await users.FindByNameAsync("reader");
            Assert.IsNotNull(reader); Assert.IsTrue((await users.UpdateSecurityStampAsync(reader)).Succeeded);
        }
        await page.Locator("#speed-search").FillAsync("fixture");
        await page.GetByRole(AriaRole.Button, new() { Name = "套用篩選" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "登入已過期或權限已變更" }).WaitForAsync(new() { Timeout = 60000 });
        Assert.AreEqual(0, await page.Locator(".device-table-wrap tbody tr").CountAsync());
        Assert.IsEmpty(errors);
    }, TestContext.CancellationToken);

    [TestMethod]
    public Task History_PagesFilteredResultsInBrowser() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString); factory.UseKestrel(0); factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>().CreateDbContextAsync(token))
        {
            var now = DateTimeOffset.UtcNow.AddMinutes(-52);
            for (var i = 1; i <= 51; i++)
                db.SpeedRuns.Add(new SpeedRun { RequestId = Guid.CreateVersion7(), RequestedAt = now.AddMinutes(i),
                    State = SpeedTestState.Success, Server = "Taipei test server", DownloadMbps = i });
            db.SpeedRuns.Add(new SpeedRun { RequestId = Guid.CreateVersion7(), RequestedAt = now, State = SpeedTestState.NoServer });
            await db.SaveChangesAsync(token);
        }
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync();
        await using var wasmGate = await AutoRenderGate.HoldAsync(page);
        await page.GotoAsync(factory.ClientOptions.BaseAddress.ToString().TrimEnd('/') + "/speed-tests");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("reader");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.Locator("main[data-runtime='Server']").WaitForAsync(new() { Timeout = 60000 });
        await page.Locator("#speed-search").FillAsync("Taipei");
        await page.GetByRole(AriaRole.Button, new() { Name = "套用篩選" }).ClickAsync();
        await page.WaitForFunctionAsync("() => document.querySelector('.device-pagination')?.innerText.includes('第 1 頁') && document.querySelectorAll('.device-table-wrap tbody tr').length === 50 && document.querySelector('main')?.innerText.includes('共 51 筆紀錄')");
        await page.GetByRole(AriaRole.Button, new() { Name = "下一頁" }).ClickAsync();
        await page.WaitForFunctionAsync("() => document.querySelector('.device-pagination')?.innerText.includes('第 2 頁') && document.querySelectorAll('.device-table-wrap tbody tr').length === 1");
        Assert.Contains("Taipei test server", await page.Locator(".device-table-wrap tbody tr").InnerTextAsync());
        Assert.IsFalse(await page.GetByRole(AriaRole.Button, new() { Name = "下一頁" }).IsEnabledAsync());
        var filteredUrl = await page.GetByRole(AriaRole.Link, new() { Name = "開啟目前篩選的連結" }).GetAttributeAsync("href");
        await wasmGate.ReleaseAsync();
        await page.WaitForFunctionAsync("() => performance.getEntriesByType('resource').some(x => x.name.includes('/_framework/Vigiloom.Web.Client.') && x.name.includes('.wasm') && x.responseEnd > 0)", null, new() { Timeout = 90000 });
        await AutoRenderGate.WaitForWebAssemblyCachedAsync(page);
        await page.GotoAsync(new Uri(factory.ClientOptions.BaseAddress, filteredUrl!).ToString());
        await page.Locator("main[data-runtime='WebAssembly']").WaitForAsync(new() { Timeout = 60000 });
        await page.WaitForFunctionAsync("() => document.querySelector('.device-pagination')?.innerText.includes('第 2 頁') && document.querySelectorAll('.device-table-wrap tbody tr').length === 1");
        Assert.Contains("Taipei test server", await page.Locator(".device-table-wrap tbody tr").InnerTextAsync());
    }, TestContext.CancellationToken);

    [TestMethod]
    public Task AdministratorCanConfigureScheduleAndCancelWithoutManualStart() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString); factory.UseKestrel(0); factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 390, Height = 844 } });
        var page = await context.NewPageAsync();
        var baseUrl = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        await page.GotoAsync(baseUrl + "/settings/speed-tests");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "網路測速設定", Exact = true }).WaitForAsync();
        Assert.Contains("Speedtest.net 使用條款", await page.Locator("main").InnerTextAsync());
        Assert.IsTrue(await page.GetByLabel("啟用排程測速").IsCheckedAsync());
        await page.GetByText("進階限制", new() { Exact = true }).ClickAsync();
        await page.Locator("#speed-budget").FillAsync("2");
        await page.Locator("#speed-timeout").FillAsync("40");
        await page.GetByRole(AriaRole.Button, new() { Name = "儲存測速設定" }).ClickAsync();
        await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "設定已儲存" }).WaitForAsync();
        await page.GotoAsync(baseUrl + "/speed-tests");
        Assert.AreEqual(0, await page.GetByRole(AriaRole.Link, new() { Name = "手動測速" }).CountAsync());
        var contexts = factory.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>();
        await using (var db = await contexts.CreateDbContextAsync(token))
            await db.Database.ExecuteSqlRawAsync("UPDATE network.SpeedSettings SET NextAutomaticAt = DATEADD(minute,-1,TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')) WHERE Name=N'central'", token);
        await using var scope = factory.Services.CreateAsyncScope();
        var queued = await scope.ServiceProvider.GetRequiredService<SpeedQueue>().RequestAsync(Guid.CreateVersion7(), token);
        Assert.IsNotNull(queued.RunId);
        await page.GotoAsync(baseUrl + "/speed-tests/" + queued.RunId.Value.ToString("D"));
        await page.GetByRole(AriaRole.Heading, new() { Name = "測速結果", Exact = true }).WaitForAsync();
        Assert.Contains("等待執行", await page.Locator("main").InnerTextAsync());
        await page.GetByLabel("我了解取消的影響").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "取消這筆測試" }).ClickAsync();
        await page.GetByText("已取消", new() { Exact = true }).WaitForAsync();
        Assert.Contains("已觀測數值僅代表部分結果", await page.Locator("main").InnerTextAsync());
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
    }, TestContext.CancellationToken);

    private static async Task AssertChartAsync(IPage page)
    {
        // The prerendered SVG can be disposed during hydration; inspect the current instance atomically.
        await page.WaitForFunctionAsync("""
            () => {
                const el = document.querySelector('.time-series-chart [role=img] > div');
                const chart = el && window.echarts?.getInstanceByDom(el);
                if (!chart) return false;
                const options = chart.getOption();
                const data = options.series?.[0]?.data;
                return chart.getWidth() >= el.clientWidth * 0.9 && chart.getWidth() > 200 && options.useUTC === true && options.xAxis?.[0]?.axisLabel?.hideOverlap === true &&
                    data?.length === 3 && data[0][1] === 80 && data[1][1] === null && data[2][1] === null;
            }
            """, null, new() { Timeout = 60000 });
    }
}
