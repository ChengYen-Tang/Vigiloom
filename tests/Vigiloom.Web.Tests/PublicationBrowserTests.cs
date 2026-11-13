using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vigiloom.Contracts.Network;
using Vigiloom.Modules.NetworkMonitoring.Domain;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
[DoNotParallelize]
public sealed class PublicationBrowserTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Administrator_CanPreviewPublishAndRevokeInBothRuntimes() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        factory.UseKestrel(0); factory.StartServer();
        var device = await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        var url = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var browserContext = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1486, Height = 1058 } });
        var page = await browserContext.NewPageAsync();
        await using var gate = await AutoRenderGate.HoldAsync(page);
        await page.GotoAsync($"{url}/devices/{device.Id:D}");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "公開設定", Exact = true }).ClickAsync();
        await page.Locator(".publication-editor[data-runtime='Server'][data-interactive='true']").WaitForAsync(new() { Timeout = 60000 });
        Assert.AreEqual(0, await page.GetByLabel("公開範本").CountAsync());
        try { await page.GetByLabel("公開（允許訪客查看）").CheckAsync(new() { Timeout = 10000 }); }
        catch (TimeoutException) { Assert.Fail(await page.Locator("body").InnerTextAsync()); }
        await page.GetByLabel("公開顯示名稱").FillAsync("router-127.1");
        await page.GetByRole(AriaRole.Button, new() { Name = "儲存設定" }).ClickAsync();
        await page.GetByRole(AriaRole.Alert).GetByText("名稱", new() { Exact = false }).WaitForAsync();
        await page.GetByLabel("公開顯示名稱").FillAsync("External network");
        await page.GetByRole(AriaRole.Button, new() { Name = "儲存設定" }).ClickAsync();
        await page.GetByText("公開設定已儲存。", new() { Exact = true }).WaitForAsync();

        await using (var resources = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        await using (var network = await factory.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>().CreateDbContextAsync(token))
        {
            var current = await resources.Devices.SingleAsync(x => x.Id == device.Id, token);
            var now = DateTimeOffset.UtcNow;
            network.PingTargets.Add(new PingTargetState { DeviceId = current.Id, Target = current.Address, DeviceVersion = current.Version,
                Enabled = true, DueAt = now, MeasuredAt = now, State = Reachability.Reachable, Outcome = PingOutcome.Success, LatencyMilliseconds = 12 });
            await network.SaveChangesAsync(token);
        }
        await page.ReloadAsync();
        await page.Locator(".publication-editor[data-runtime='Server'][data-interactive='true']").WaitForAsync(new() { Timeout = 60000 });
        await page.GetByText("12 ms", new() { Exact = true }).WaitForAsync();
        if (Environment.GetEnvironmentVariable("VIGILOOM_PUBLICATION_UI_EVIDENCE_DIR") is { Length: > 0 } evidence)
        {
            Directory.CreateDirectory(evidence);
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "desktop.png"), FullPage = true });
            await page.SetViewportSizeAsync(390, 844);
            await page.WaitForFunctionAsync("() => document.documentElement.scrollWidth <= innerWidth");
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "mobile.png"), FullPage = true });
        }
        await gate.ReleaseAsync();
        await AutoRenderGate.WaitForWebAssemblyCachedAsync(page);
        await page.RouteAsync("**/api/v1/admin/publications/**/preview", route => route.FulfillAsync(new() { Status = 503 }));
        await page.ReloadAsync();
        await page.Locator(".publication-editor[data-runtime='WebAssembly'][data-interactive='true']").WaitForAsync(new() { Timeout = 60000 });
        await page.GetByRole(AriaRole.Alert).GetByText("預覽暫時無法讀取", new() { Exact = false }).WaitForAsync();
        await page.GetByLabel("不公開（僅登入後可查看）").CheckAsync();
        Assert.AreEqual(0, await page.GetByLabel("公開範本").CountAsync());
        await page.GetByRole(AriaRole.Button, new() { Name = "儲存設定" }).ClickAsync();
        await page.GetByText("公開設定已儲存", new() { Exact = false }).WaitForAsync();
        using var anonymous = new HttpClient { BaseAddress = new Uri(url) };
        using var response = await anonymous.GetAsync($"/api/v1/public/devices/{device.Id:D}", token);
        Assert.AreEqual(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }, context.CancellationToken);
}
