extern alias WorkerHostAssembly;
using Hangfire;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Testing.SpeedTests;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;
using WorkerHost = WorkerHostAssembly::Vigiloom.Worker.Hosting.WorkerHost;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
[DoNotParallelize]
public sealed class SpeedJourneyTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public Task AdministratorRunsMeasurementAndReadsHistory() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        GlobalConfiguration.Configuration.UseNoOpLogProvider();
        await HangfireSchemaInstaller.InstallAsync(database.ConnectionString, token);
        var scheduling = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
        await using (var db = new SchedulingDbContext(scheduling)) await db.Database.MigrateAsync(token);
        using var web = PingTestSite.CreateFactory(database.ConnectionString);
        web.UseKestrel(0);
        web.StartServer();
        await PingTestSite.InitializeAsync(web, token);

        var builder = WorkerHost.CreateBuilder([]);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Vigiloom"] = database.ConnectionString });
        builder.Services.AddSingleton<IHttpMessageHandlerFactory>(new ControlledPhaseFactory(new ControlledPhaseHandler()));
        using var worker = builder.Build();
        try
        {
            await worker.StartAsync(token);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync();
            await using var wasmGate = await AutoRenderGate.HoldAsync(page);
            var baseUrl = web.ClientOptions.BaseAddress.ToString().TrimEnd('/');
            await page.GotoAsync(baseUrl + "/settings/speed-tests");
            await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
            await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
            await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
            await page.GetByLabel("我了解測試將連線至第三方服務", new() { Exact = false }).CheckAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "儲存測速設定" }).ClickAsync();
            await page.GetByRole(AriaRole.Status).Filter(new() { HasText = "設定已儲存" }).WaitForAsync();
            await page.GotoAsync(baseUrl + "/speed-tests/start");
            await page.GetByLabel("我確認現在從中央位置進行測試", new() { Exact = false }).CheckAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "開始測速" }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "測速結果", Exact = true }).WaitForAsync();
            var id = Guid.Parse(new Uri(page.Url).Segments.Last());
            var contexts = web.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            while (true)
            {
                await using var db = await contexts.CreateDbContextAsync(deadline.Token);
                var run = await db.SpeedRuns.AsNoTracking().SingleAsync(x => x.Id == id, deadline.Token);
                if (run.State is not (SpeedTestState.Queued or SpeedTestState.Running))
                {
                    Assert.AreEqual(SpeedTestState.Success, run.State, $"Diagnostic: {run.DiagnosticCode}");
                    break;
                }
                await Task.Delay(250, deadline.Token);
            }
            await page.ReloadAsync();
            await page.GetByText("完成", new() { Exact = true }).WaitForAsync();
            Assert.Contains("Example (#1) · speedtest.example.com", await page.Locator("main").InnerTextAsync());
            await page.GotoAsync(baseUrl + "/speed-tests");
            await page.Locator("main[data-runtime='Server']").WaitForAsync(new() { Timeout = 60000 });
            var row = page.Locator(".device-table-wrap tbody tr").First;
            await row.WaitForAsync();
            Assert.Contains("Example (#1) · speedtest.example.com", await row.InnerTextAsync());
            Assert.Contains(id.ToString("D"), (await row.GetByRole(AriaRole.Link, new() { Name = "查看結果" }).GetAttributeAsync("href"))!);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }, TestContext.CancellationToken);
}
