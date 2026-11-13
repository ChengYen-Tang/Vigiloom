using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vigiloom.Contracts.Network;
using Vigiloom.Contracts.Workspace;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.NetworkMonitoring.Domain;
using Vigiloom.Modules.NetworkMonitoring.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
public sealed class WorkspaceOverviewTests(TestContext context)
{
    [TestMethod]
    [TestCategory("Browser")]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Workspace_RendersIndependentSectionsInBothRuntimes() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        factory.UseKestrel(0); factory.StartServer();
        var device = await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        var now = DateTimeOffset.UtcNow;
        var host = new Device("Taipei database", "host.example", true, now);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        {
            var group = new Group("Taipei", now);
            db.Devices.Add(host); db.Groups.Add(group);
            db.DeviceGroups.AddRange(new DeviceGroup(group.Id, device.Id), new DeviceGroup(group.Id, host.Id));
            await db.SaveChangesAsync(token);
        }
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>().CreateDbContextAsync(token))
        {
            db.PingTargets.AddRange(
                new PingTargetState { DeviceId = device.Id, Target = device.Address, DeviceVersion = device.Version, Enabled = true, DueAt = now, MeasuredAt = now, State = Reachability.Unreachable, Outcome = PingOutcome.Timeout },
                new PingTargetState { DeviceId = host.Id, Target = host.Address, DeviceVersion = host.Version, Enabled = true, DueAt = now, MeasuredAt = now, State = Reachability.Reachable, Outcome = PingOutcome.Success, LatencyMilliseconds = 4 });
            await db.SaveChangesAsync(token);
        }
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
        {
            db.Agents.Add(new Vigiloom.Modules.AgentManagement.Domain.Agent(host.Id, host.AgentAuthorizationGeneration, new byte[32], now.AddMinutes(-10)));
            await db.SaveChangesAsync(token);
        }
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
        {
            var source = new ServiceSource("source", new byte[32], now);
            var candidate = new ServiceCandidate(source.Id, "orders", "prod", "Orders", now);
            db.Sources.Add(source); db.Candidates.Add(candidate); db.Services.Add(new MonitoredService(candidate, now));
            await db.SaveChangesAsync(token);
        }
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var browserContext = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1486, Height = 1058 } });
        var page = await browserContext.NewPageAsync();
        await using var gate = await AutoRenderGate.HoldAsync(page);
        var url = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        await page.GotoAsync(url + "/workspace");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("reader");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.Locator(".workspace[data-runtime='Server']").WaitForAsync(new() { Timeout = 60000 });
        await page.GetByRole(AriaRole.Heading, new() { Name = "設備 Ping" }).WaitForAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = device.Name }).WaitForAsync();
        await page.GetByText("離線", new() { Exact = true }).WaitForAsync();
        await page.GetByText("未知或資料不足", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "Taipei", Exact = true }).WaitForAsync();
        if (Environment.GetEnvironmentVariable("VIGILOOM_WORKSPACE_UI_EVIDENCE_DIR") is { Length: > 0 } evidence)
        {
            Directory.CreateDirectory(evidence);
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "desktop.png"), FullPage = true });
            await page.SetViewportSizeAsync(390, 844);
            await page.WaitForFunctionAsync("() => document.documentElement.scrollWidth <= window.innerWidth");
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "mobile.png"), FullPage = true });
        }
        await page.SetViewportSizeAsync(390, 844);
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth"));
        await gate.ReleaseAsync();
        await AutoRenderGate.WaitForWebAssemblyCachedAsync(page);
        await page.ReloadAsync();
        await page.Locator(".workspace[data-runtime='WebAssembly']").WaitForAsync(new() { Timeout = 60000 });
        await page.GetByRole(AriaRole.Link, new() { Name = device.Name }).WaitForAsync(new() { Timeout = 30000 });
        await page.GetByRole(AriaRole.Link, new() { Name = device.Name }).ClickAsync();
        await page.WaitForURLAsync($"**/devices/{device.Id:D}");
    }, context.CancellationToken);

    [TestMethod]
    public Task Reader_SeesIndependentStatesAndAbnormalDevicesAcrossPages() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        var now = DateTimeOffset.UtcNow;
        Device host; Device critical;
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        {
            db.Devices.AddRange(Enumerable.Range(0, 497).Select(i => new Device($"Router {i:D3}", $"node{i}.example", false, now)));
            host = new Device("ZZZ host", "host.example", true, now);
            critical = new Device("ZZZ router", "router.example", false, now);
            db.Devices.AddRange(host, critical);
            var groupA = new Group("A", now); var groupB = new Group("B", now);
            db.Groups.AddRange(groupA, groupB);
            db.DeviceGroups.AddRange(new DeviceGroup(groupA.Id, host.Id), new DeviceGroup(groupB.Id, host.Id));
            await db.SaveChangesAsync(token);
        }
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<NetworkDbContext>>().CreateDbContextAsync(token))
        {
            db.PingTargets.Add(new PingTargetState { DeviceId = host.Id, Target = host.Address, DeviceVersion = host.Version,
                Enabled = true, DueAt = now, MeasuredAt = now, State = Reachability.Reachable, Outcome = PingOutcome.Success, LatencyMilliseconds = 12 });
            db.PingTargets.Add(new PingTargetState { DeviceId = critical.Id, Target = critical.Address, DeviceVersion = critical.Version,
                Enabled = true, DueAt = now, MeasuredAt = now, State = Reachability.Unreachable, Outcome = PingOutcome.Timeout });
            await db.SaveChangesAsync(token);
        }
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
        {
            db.Agents.Add(new Vigiloom.Modules.AgentManagement.Domain.Agent(host.Id, host.AgentAuthorizationGeneration, new byte[32], now.AddMinutes(-10)));
            await db.SaveChangesAsync(token);
        }
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
        {
            var source = new ServiceSource("source", new byte[32], now);
            var candidate = new ServiceCandidate(source.Id, "orders", "prod", "Orders", now);
            var stoppedCandidate = new ServiceCandidate(source.Id, "stopped", "prod", "Stopped", now);
            var stopped = new MonitoredService(stoppedCandidate, now); stopped.Revoke(now);
            db.Sources.Add(source); db.Candidates.AddRange(candidate, stoppedCandidate);
            db.Services.AddRange(new MonitoredService(candidate, now), stopped);
            await db.SaveChangesAsync(token);
        }
        using var client = PingTestSite.Client(factory);
        using (var anonymous = await client.GetAsync("/api/v1/workspace/overview", token)) Assert.AreEqual(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await PingTestSite.LoginAsync(client, "reader", token);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using var response = await client.GetAsync("/api/v1/workspace/overview", token);
        context.WriteLine($"Overview of 500 devices returned HTTP headers in {stopwatch.ElapsedMilliseconds} ms.");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue(response.Headers.CacheControl?.NoStore);
        var result = await response.Content.ReadFromJsonAsync<WorkspaceOverview>(cancellationToken: token);
        Assert.IsNotNull(result);
        Assert.IsFalse(result.Ping.Unavailable); Assert.IsFalse(result.Agents.Unavailable); Assert.IsFalse(result.Services.Unavailable);
        Assert.AreEqual(500, result.Ping.Total);
        Assert.AreEqual(critical.Id, result.Ping.Items[0].Id, "An abnormal device from a later page must be prioritized.");
        Assert.AreEqual(1, result.Agents.Total);
        Assert.AreEqual(host.Id, result.Agents.Items[0].Id);
        Assert.AreEqual("離線", result.Agents.Items[0].State);
        Assert.AreEqual(2, result.Services.Total);
        Assert.AreEqual("未知或資料不足", result.Services.Items[0].State);
        Assert.AreEqual("收集已停止", result.Services.Items[1].State);
        Assert.AreEqual(1, result.Services.Attention);
        Assert.HasCount(2, result.Groups);
        Assert.IsFalse(result.Ping.Items.Any(x => x.Id == host.Id), "A reachable item on a later page must not displace unknown devices.");
    }, context.CancellationToken);
}
