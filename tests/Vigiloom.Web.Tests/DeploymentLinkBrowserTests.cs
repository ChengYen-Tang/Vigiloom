using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vigiloom.Contracts.Services;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Web.ServiceDetails;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
[DoNotParallelize]
public sealed class DeploymentLinkBrowserTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Reader_SeesAttestedHostInBothRenderModes_AdministratorCanUseMobileSettings() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var host = PingTestSite.CreateFactory(database.ConnectionString);
        host.UseKestrel(0); host.StartServer();
        var device = await PingTestSite.InitializeAsync(host, token, schemaInitialized: true);
        var now = DateTimeOffset.UtcNow;
        var source = new ServiceSource("browser", new byte[32], now);
        var candidate = new ServiceCandidate(source.Id, "orders", "test", "Orders", now);
        var service = new MonitoredService(candidate, now);
        var instanceId = Guid.CreateVersion7();
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
        {
            db.Sources.Add(source); db.Candidates.Add(candidate); db.Services.Add(service); await db.SaveChangesAsync(token);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [service].[Instances] ([Id],[ServiceId],[SourceScopeId],[ExternalInstanceId],[CreatedAt],[UpdatedAt]) VALUES ({instanceId},{service.Id},{source.Id},{"node"},{now},{now})", token);
        }
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<DeploymentLinks>().CreateAsync(service.Id,
                new CreateDeploymentLink(device.Id, instanceId, "Verified configuration"), Guid.CreateVersion7(), token);
            Assert.AreEqual(DeploymentLinkStatus.Saved, result.Status);
        }
        var url = host.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var readerContext = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 390, Height = 844 } });
        var page = await readerContext.NewPageAsync();
        await using var gate = await AutoRenderGate.HoldAsync(page);
        await page.GotoAsync($"{url}/services/{service.Id:D}");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("reader");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.Locator(".service-overview[data-runtime='Server'][data-interactive='true']").WaitForAsync(new() { Timeout = 60000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "查看關聯主機" }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = device.Name, Exact = true }).WaitForAsync();
        await page.GetByText("Verified configuration", new() { Exact = true }).WaitForAsync();
        Assert.AreEqual(0, await page.GetByRole(AriaRole.Link, new() { Name = "管理部署關聯" }).CountAsync());
        await gate.ReleaseAsync();
        await AutoRenderGate.WaitForWebAssemblyCachedAsync(page);
        await page.ReloadAsync();
        await page.Locator(".service-overview[data-runtime='WebAssembly'][data-interactive='true']").WaitForAsync(new() { Timeout = 60000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "查看關聯主機" }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = device.Name, Exact = true }).WaitForAsync();
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));

        await using var adminContext = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 390, Height = 844 } });
        var admin = await adminContext.NewPageAsync();
        await admin.GotoAsync($"{url}/settings/service-deployments?service={service.Id:D}");
        await admin.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
        await admin.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await admin.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        var table = admin.GetByRole(AriaRole.Region, new() { Name = "部署關聯，可左右捲動" });
        await table.GetByText("Verified configuration", new() { Exact = true }).WaitForAsync();
        await admin.GetByText("左右滑動查看完整關聯資訊。", new() { Exact = true }).WaitForAsync();
        Assert.IsTrue(await admin.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        Assert.IsTrue(await table.EvaluateAsync<bool>("e => e.scrollWidth > e.clientWidth && getComputedStyle(e).overflowX === 'auto'"));
        if (Environment.GetEnvironmentVariable("VIGILOOM_DEPLOYMENT_UI_EVIDENCE_DIR") is { Length: > 0 } evidence)
        {
            Directory.CreateDirectory(evidence);
            await admin.ScreenshotAsync(new() { Path = Path.Combine(evidence, "deployment-mobile.png"), FullPage = true });
            await admin.SetViewportSizeAsync(1486, 1058);
            await admin.ScreenshotAsync(new() { Path = Path.Combine(evidence, "deployment-desktop.png"), FullPage = true });
        }
    }, context.CancellationToken);
}
