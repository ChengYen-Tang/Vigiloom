extern alias NetworkHost;
using Hangfire;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using Vigiloom.Contracts.SpeedTests;
using Vigiloom.Infrastructure.Scheduling;
using Vigiloom.Infrastructure.Scheduling.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Application.SpeedTests;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Testing.SpeedTests;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

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
    public Task ScheduledMeasurementAppearsInHistory() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        GlobalConfiguration.Configuration.UseNoOpLogProvider();
        await HangfireSchemaInstaller.InstallAsync(database.ConnectionString, token);
        var scheduling = new DbContextOptionsBuilder<SchedulingDbContext>().UseSqlServer(database.ConnectionString,
            sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "scheduling")).Options;
        await using (var db = new SchedulingDbContext(scheduling)) await db.Database.MigrateAsync(token);
        await using (var db = new NetworkDbContext(new DbContextOptionsBuilder<NetworkDbContext>()
            .UseSqlServer(database.ConnectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", "network")).Options))
            await db.Database.MigrateAsync(token);
        using var network = new WebApplicationFactory<NetworkHost::Program>().WithWebHostBuilder(builder =>
        {
            builder.UseUrls("http://127.0.0.1:0");
            builder.UseSetting("ConnectionStrings:Network", database.ConnectionString);
            builder.UseSetting("Services:Resources:Url", "http://127.0.0.1:1");
            builder.UseSetting("Hangfire:Enabled", "false");
            builder.UseSetting("Temporal:EnableNetworkWorker", "false");
            builder.ConfigureTestServices(services =>
            {
                foreach (var item in services.Where(x => x.ServiceType == typeof(IHostedService) &&
                    x.ImplementationType?.Name is "WorkDispatcher" or "PingWorker" or "NetworkDiagnosticsPublisher").ToArray()) services.Remove(item);
                services.AddSingleton<IHttpMessageHandlerFactory>(new ControlledPhaseFactory(new ControlledPhaseHandler()));
                services.RemoveAll<ISpeedProbe>();
                services.AddScoped<ISpeedProbe>(provider => new SpeedtestNetProbe(
                    provider.GetRequiredService<IHttpMessageHandlerFactory>(), 2_000, 4, defaultMultiServer: true));
            });
        });
        network.UseKestrel(0);
        using var networkClient = network.CreateClient();
        using var web = PingTestSite.CreateFactory(database.ConnectionString).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Services:Network:Url", networkClient.BaseAddress!.ToString());
            builder.UseSetting("Services:Resources:Url", "http://127.0.0.1:1");
            builder.UseSetting("Services:Observability:Url", "http://127.0.0.1:1");
        });
        web.UseKestrel(0);
        web.StartServer();
        await PingTestSite.InitializeAsync(web, token);

        {
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            var page = await browser.NewPageAsync();
            await using var wasmGate = await AutoRenderGate.HoldAsync(page);
            var baseUrl = web.ClientOptions.BaseAddress.ToString().TrimEnd('/');
            await page.GotoAsync(baseUrl + "/settings/speed-tests");
            await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
            await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
            await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Heading, new() { Name = "網路測速設定", Exact = true }).WaitForAsync();
            var contexts = web.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>();
            await using (var db = await contexts.CreateDbContextAsync(token))
                await db.Database.ExecuteSqlRawAsync("UPDATE network.SpeedSettings SET NextAutomaticAt = DATEADD(minute,-1,TODATETIMEOFFSET(SYSUTCDATETIME(), '+00:00')) WHERE Name=N'central'", token);
            await using var scope = network.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<SpeedQueue>().RequestAsync(Guid.CreateVersion7(), token);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            Guid id;
            while (true)
            {
                await using var db = await contexts.CreateDbContextAsync(deadline.Token);
                var run = await db.SpeedRuns.AsNoTracking().SingleAsync(deadline.Token);
                if (run.State is not (SpeedTestState.Queued or SpeedTestState.Running))
                {
                    Assert.AreEqual(SpeedTestState.Success, run.State, $"Diagnostic: {run.DiagnosticCode}");
                    Assert.IsTrue(run.DownloadMbps is double download && double.IsFinite(download) && download > 0);
                    Assert.IsTrue(run.UploadMbps is double upload && double.IsFinite(upload) && upload > 0);
                    Assert.IsTrue(run.Bytes is > 0);
                    id = run.Id;
                    break;
                }
                await Task.Delay(250, deadline.Token);
            }
            await page.GotoAsync(baseUrl + "/speed-tests/" + id.ToString("D"));
            await page.GetByText("完成", new() { Exact = true }).WaitForAsync();
            Assert.Contains("Example (#1) · speedtest.example.com", await page.Locator("main").InnerTextAsync());
            await page.GotoAsync(baseUrl + "/speed-tests");
            await page.Locator("main[data-runtime='Server']").WaitForAsync(new() { Timeout = 60000 });
            var row = page.Locator(".device-table-wrap tbody tr").First;
            await row.WaitForAsync();
            Assert.Contains("Example (#1) · speedtest.example.com", await row.InnerTextAsync());
            Assert.Contains(id.ToString("D"), (await row.GetByRole(AriaRole.Link, new() { Name = "查看結果" }).GetAttributeAsync("href"))!);
        }
    }, TestContext.CancellationToken);
}
