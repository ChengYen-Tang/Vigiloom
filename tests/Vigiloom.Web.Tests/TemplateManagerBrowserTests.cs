using Microsoft.AspNetCore.Hosting;
using Microsoft.Playwright;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Vigiloom.Modules.Publishing.Domain;
using Vigiloom.Modules.Publishing.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
public sealed class TemplateManagerBrowserTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task HistoryPreview_UsesPublishedDataInBothRuntimes() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        host.UseKestrel(0); host.StartServer();
        var device = await PingTestSite.InitializeAsync(host, token, schemaInitialized: true);
        var now = DateTimeOffset.UtcNow;
        var template = new PublicationTemplate("Network history", true, true, now, 7);
        template.ConfirmHistorySince(now.AddDays(-3));
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>().CreateDbContextAsync(token))
        { db.Templates.Add(template); await db.SaveChangesAsync(token); }
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        {
            var row = await db.Devices.SingleAsync(x => x.Id == device.Id, token);
            row.SetTemplatePublication("Public network", template.Id, false, now);
            row.ConfirmPublicSince(now.AddDays(-3));
            await db.SaveChangesAsync(token);
        }
        var bucket = now.AddTicks(-(now.UtcTicks % TimeSpan.FromMinutes(5).Ticks)).AddDays(-2);
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>().CreateDbContextAsync(token))
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [network].[PingRollups] ([DeviceId], [Target], [Location], [WindowStart], [Attempts], [Successes], [Timeouts], [Unreachable], [DnsErrors], [PermissionDenied], [MonitorErrors], [UnknownFailures], [LatencySum], [LatencyOverflow], [FirstOccurredAt], [LastOccurredAt])
                VALUES ({device.Id}, {device.Address}, {"central"}, {bucket}, 1, 1, 0, 0, 0, 0, 0, 0, {12.34d}, 0, {bucket}, {bucket})
                """, token);
        var url = host.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var browserContext = await browser.NewContextAsync();
        var page = await browserContext.NewPageAsync();
        await using var gate = await AutoRenderGate.HoldAsync(page);
        await page.GotoAsync($"{url}/settings/publication-templates");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.Locator(".templates[data-runtime='Server'][data-interactive='true']").WaitForAsync(new() { Timeout = 60000 });
        await AssertPreviewAsync();
        await gate.ReleaseAsync();
        await AutoRenderGate.WaitForWebAssemblyCachedAsync(page);
        await page.ReloadAsync();
        await page.Locator(".templates[data-runtime='WebAssembly'][data-interactive='true']").WaitForAsync(new() { Timeout = 60000 });
        await AssertPreviewAsync();
        await page.GotoAsync($"{url}/devices/{device.Id:D}");
        await page.GetByRole(AriaRole.Link, new() { Name = "公開設定", Exact = true }).ClickAsync();
        await page.Locator(".publication-editor[data-runtime='WebAssembly'][data-interactive='true']").WaitForAsync(new() { Timeout = 60000 });
        await page.GetByText("最新歷史：", new() { Exact = false }).WaitForAsync();
        var devicePreview = await page.Locator(".publication-editor aside").InnerTextAsync();
        Assert.Contains("12.34 ms", devicePreview);
        Assert.DoesNotContain(device.Address, devicePreview);
        await page.GetByLabel("公開範本").SelectOptionAsync("baseline-v1");
        await page.GetByText("不公開歷史。", new() { Exact = false }).WaitForAsync();
        Assert.DoesNotContain("最新歷史：", await page.Locator(".publication-editor aside").InnerTextAsync());

        async Task AssertPreviewAsync()
        {
            await page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Network history" }).GetByRole(AriaRole.Button, new() { Name = "編輯" }).ClickAsync();
            await page.GetByLabel("歷史平均延遲").SelectOptionAsync("3");
            await page.GetByRole(AriaRole.Button, new() { Name = "預覽變更" }).ClickAsync();
            await page.Locator(".history-comparison").GetByText("12.34 ms", new() { Exact = false }).First.WaitForAsync();
            await page.Locator(".history-comparison > div").Last.GetByText("12.34 ms", new() { Exact = false }).WaitForAsync();
            var text = await page.Locator(".history-comparison").InnerTextAsync();
            Assert.DoesNotContain(device.Address, text);
            await page.GetByRole(AriaRole.Button, new() { Name = "返回修改" }).ClickAsync();
            await page.GetByLabel("歷史平均延遲").SelectOptionAsync("1");
            await page.GetByRole(AriaRole.Button, new() { Name = "預覽變更" }).ClickAsync();
            await page.Locator(".history-comparison").GetByText("此範圍沒有可顯示的歷史資料。", new() { Exact = true }).WaitForAsync();
        }
    }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task ImpactPreview_PaginatesAndRejectsChangedMembership() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        host.UseKestrel(0); host.StartServer();
        await PingTestSite.InitializeAsync(host, token, schemaInitialized: true);
        var now = DateTimeOffset.UtcNow;
        var first = new PublicationTemplate("Alpha", true, false, now);
        var second = new PublicationTemplate("Beta", false, false, now);
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>().CreateDbContextAsync(token))
        { db.Templates.AddRange(first, second); await db.SaveChangesAsync(token); }
        var devices = Enumerable.Range(0, 51).Select(i => new Device($"Device {i:D2}", "example.com", false, now)).ToArray();
        foreach (var device in devices) device.SetTemplatePublication($"Public sample {device.Name}", first.Id, false, now);
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        { db.Devices.AddRange(devices); await db.SaveChangesAsync(token); }
        var url = host.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var browserContext = await browser.NewContextAsync();
        var page = await browserContext.NewPageAsync();
        await using var gate = await AutoRenderGate.HoldAsync(page);
        await page.GotoAsync($"{url}/settings/publication-templates");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.Locator(".templates[data-runtime='Server'][data-interactive='true']").WaitForAsync(new() { Timeout = 60000 });
        await page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Alpha" }).GetByText("51", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Alpha" }).GetByRole(AriaRole.Button, new() { Name = "編輯" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "預覽變更" }).ClickAsync();
        await page.GetByText("Public sample", new() { Exact = false }).First.WaitForAsync();
        var firstSample = await page.Locator(".comparison").InnerTextAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "下一頁" }).ClickAsync();
        await page.GetByText("第 2 頁", new() { Exact = true }).WaitForAsync();
        var secondSample = await page.Locator(".comparison").InnerTextAsync();
        Assert.AreNotEqual(firstSample, secondSample);
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        { var row = await db.Devices.SingleAsync(x => x.Id == devices[0].Id, token); row.Rename("Changed", DateTimeOffset.UtcNow); await db.SaveChangesAsync(token); }
        await page.GetByRole(AriaRole.Button, new() { Name = "上一頁" }).ClickAsync();
        await page.GetByRole(AriaRole.Alert).GetByText("請重新預覽", new() { Exact = false }).WaitForAsync();
        await page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Beta" }).GetByRole(AriaRole.Button, new() { Name = "編輯" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "預覽變更" }).ClickAsync();
        await page.GetByText("目前這一頁沒有可顯示的公開設備資料。", new() { Exact = true }).WaitForAsync();
        Assert.AreEqual(0, await page.GetByText("Public sample", new() { Exact = false }).CountAsync());
    }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Administrator_ManagesTemplatesInBothRuntimes() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        host.UseKestrel(0); host.StartServer();
        await PingTestSite.InitializeAsync(host, token, schemaInitialized: true);
        var url = host.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var browserContext = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1487, Height = 1058 } });
        var page = await browserContext.NewPageAsync();
        await using var gate = await AutoRenderGate.HoldAsync(page);
        await page.GotoAsync($"{url}/settings/publication-templates");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.Locator(".templates[data-runtime='Server'][data-interactive='true']").WaitForAsync(new() { Timeout = 60000 });
        await page.GetByText("尚無公開範本", new() { Exact = false }).WaitForAsync();
        await CreateAsync("Alpha");
        var alpha = page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Alpha" });
        await alpha.GetByRole(AriaRole.Button, new() { Name = "設為預設" }).ClickAsync();
        await page.GetByText("預設範本已更新。", new() { Exact = true }).WaitForAsync();
        await CreateAsync("Beta");
        alpha = page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Alpha" });
        await alpha.GetByRole(AriaRole.Button, new() { Name = "編輯" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "編輯範本", Exact = true }).WaitForAsync();
        Assert.AreEqual("rgb(23, 66, 124)", await alpha.GetByRole(AriaRole.Button, new() { Name = "刪除" }).EvaluateAsync<string>("element => getComputedStyle(element).color"));
        var evidence = Environment.GetEnvironmentVariable("VIGILOOM_TEMPLATE_UI_EVIDENCE_DIR");
        if (!string.IsNullOrEmpty(evidence))
        {
            Directory.CreateDirectory(evidence);
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "desktop.png") });
        }
        await page.SetViewportSizeAsync(390, 844);
        await page.WaitForFunctionAsync("() => document.documentElement.scrollWidth <= innerWidth");
        Assert.IsTrue(await page.GetByText("左右滑動以查看設備數與操作。", new() { Exact = true }).IsVisibleAsync());
        if (!string.IsNullOrEmpty(evidence)) await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "mobile.png") });
        await page.SetViewportSizeAsync(1487, 1058);
        await page.GetByLabel("延遲時間").CheckAsync();
        await page.GetByLabel("歷史平均延遲").SelectOptionAsync("3");
        await page.GetByRole(AriaRole.Button, new() { Name = "預覽變更" }).ClickAsync();
        await page.GetByText("確認變更", new() { Exact = true }).WaitForAsync();
        await page.GetByText("歷史平均延遲：不公開 → 最近 3 天。", new() { Exact = true }).WaitForAsync();
        await page.GetByText("從變更生效時開始累積可公開資料", new() { Exact = false }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "確認儲存" }).ClickAsync();
        await page.GetByText("範本已更新。", new() { Exact = true }).WaitForAsync();
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<PublishingDbContext>>().CreateDbContextAsync(token))
        { var saved = await db.Templates.AsNoTracking().SingleAsync(x => x.Name == "Alpha", token); Assert.AreEqual(3, saved.HistoryDays); Assert.AreNotEqual(PublicationTemplate.PendingHistorySince, saved.HistorySince); }
        alpha = page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Alpha" });
        await alpha.GetByRole(AriaRole.Button, new() { Name = "刪除" }).ClickAsync();
        await page.GetByLabel("替代預設範本").SelectOptionAsync(new SelectOptionValue { Label = "Beta" });
        await page.GetByRole(AriaRole.Button, new() { Name = "預覽影響" }).ClickAsync();
        await page.GetByText("替代預設：Beta", new() { Exact = false }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "確認刪除" }).ClickAsync();
        await page.GetByText("範本已刪除。", new() { Exact = true }).WaitForAsync();
        await gate.ReleaseAsync();
        await AutoRenderGate.WaitForWebAssemblyCachedAsync(page);
        await page.ReloadAsync();
        await page.Locator(".templates[data-runtime='WebAssembly'][data-interactive='true']").WaitForAsync(new() { Timeout = 60000 });
        await page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Beta" }).GetByText("預設", new() { Exact = true }).WaitForAsync();
        await CreateAsync("Gamma", 7);
        await page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Gamma" }).WaitForAsync();
        await page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Gamma" }).GetByText("歷史平均延遲（7 天）", new() { Exact = false }).WaitForAsync();
        await page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Gamma" }).GetByRole(AriaRole.Button, new() { Name = "設為預設" }).ClickAsync();
        await page.GetByText("預設範本已更新。", new() { Exact = true }).WaitForAsync();
        var beta = page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Beta" });
        await beta.GetByRole(AriaRole.Button, new() { Name = "編輯" }).ClickAsync();
        await page.GetByLabel("觀測時間").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "預覽變更" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "確認儲存" }).ClickAsync();
        await page.GetByText("範本已更新。", new() { Exact = true }).WaitForAsync();
        beta = page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Beta" });
        await beta.GetByRole(AriaRole.Button, new() { Name = "刪除" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "預覽影響" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "確認刪除" }).ClickAsync();
        await page.GetByText("範本已刪除。", new() { Exact = true }).WaitForAsync();

        async Task CreateAsync(string name, int days = 0)
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "新增範本" }).ClickAsync();
            await page.GetByLabel("範本名稱").FillAsync(name);
            if (days > 0) await page.GetByLabel("歷史平均延遲").SelectOptionAsync(days.ToString());
            await page.GetByRole(AriaRole.Button, new() { Name = "建立範本" }).ClickAsync();
            await page.GetByText("範本已建立。", new() { Exact = true }).WaitForAsync();
        }
    }, context.CancellationToken);
}
