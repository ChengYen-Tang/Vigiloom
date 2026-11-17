using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
[DoNotParallelize]
public sealed class GroupAgentRolloutBrowserTests(TestContext context)
{
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public Task AdministratorCanPreviewAndPauseAGroupRollout() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        factory.UseKestrel(0);
        factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        var group = new Group("測試群組", DateTimeOffset.UtcNow);
        var devices = new[] { new Device("主要主機", "127.0.1.1", true, DateTimeOffset.UtcNow), new Device("次要主機", "127.0.1.2", true, DateTimeOffset.UtcNow) };
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        {
            db.Groups.Add(group); db.Devices.AddRange(devices);
            db.DeviceGroups.AddRange(devices.Select(x => new DeviceGroup(group.Id, x.Id)));
            await db.SaveChangesAsync(token);
        }
        using var scope = factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<AgentIdentity>();
        foreach (var device in devices)
        {
            var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
            var registered = await identity.RedeemAsync(issued.Secret, SHA256.HashData(RandomNumberGenerator.GetBytes(32)), token);
            await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token);
            var agent = await db.Agents.SingleAsync(x => x.Id == registered.AgentId, token);
            agent.RecordRuntime(new AgentRuntimeReport("1.0.0", "win-x64"), DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(token);
        }
        var packageId = Guid.CreateVersion7();
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
        {
            db.Packages.Add(new AgentPackage { Id = packageId, Version = "1.2.3", RuntimeIdentifier = "win-x64", Size = 1,
                Sha256 = new string('a', 64), ManifestSha256 = new string('b', 64), Listed = true, VerifiedUntil = DateTimeOffset.UtcNow.AddDays(1) });
            await db.SaveChangesAsync(token);
        }
        var url = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var administrator = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1024 } });
        var page = await administrator.NewPageAsync();
        await page.GotoAsync(url + $"/groups/{group.Id:D}/agent-updates");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.Locator(".rollouts[data-interactive='true']").WaitForAsync();
        await page.Locator("#rollout-package").SelectOptionAsync(packageId.ToString());
        await page.GetByLabel("每批設備數").FillAsync("200");
        await page.GetByRole(AriaRole.Button, new() { Name = "預覽適用設備" }).ClickAsync();
        await page.GetByText("適用：2 台；未納入：0 台（win-x64）。", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "開始分批更新" }).ClickAsync();
        await page.GetByText("已建立更新：納入 2 台，未納入 0 台。", new() { Exact = true }).WaitForAsync();
        Assert.Contains("後續每批 200 台", await page.Locator("main").InnerTextAsync());
        await scope.ServiceProvider.GetRequiredService<AgentRollouts>().AdvanceAsync(token);
        await page.GetByRole(AriaRole.Button, new() { Name = "重新整理" }).ClickAsync();
        await page.GetByText("已指派，等待結果", new() { Exact = true }).WaitForAsync();
        await page.GetByText("等待指派", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "暫停後續指派" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "繼續更新" }).WaitForAsync();
        Assert.Contains("已經指派的設備仍可能完成更新", await page.Locator("main").InnerTextAsync());
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
        {
            var active = await db.RolloutMembers.SingleAsync(x => x.Status == AgentRolloutMemberStatus.Assigned, token);
            db.UpdateReceipts.Add(new AgentUpdateReceipt { AgentId = active.AgentId, UpdateId = active.UpdateId!.Value,
                MessageId = Guid.CreateVersion7(), Sequence = 1, Result = AgentUpdateResultKind.Succeeded,
                OccurredAt = DateTimeOffset.UtcNow, ReceivedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(token);
        }
        await scope.ServiceProvider.GetRequiredService<AgentRollouts>().AdvanceAsync(token);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
            Assert.AreEqual(1, await db.RolloutMembers.CountAsync(x => x.Status == AgentRolloutMemberStatus.Pending, token));
        await page.GetByRole(AriaRole.Button, new() { Name = "取消後續指派" }).ClickAsync();
        await page.GetByText("已取消", new() { Exact = true }).First.WaitForAsync();
        await scope.ServiceProvider.GetRequiredService<AgentRollouts>().AdvanceAsync(token);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
            Assert.AreEqual(1, await db.RolloutMembers.CountAsync(x => x.Status == AgentRolloutMemberStatus.Pending, token));
        await using (var readerContext = await browser.NewContextAsync())
        {
            var reader = await readerContext.NewPageAsync();
            await reader.GotoAsync(url + $"/groups/{group.Id:D}/agent-updates");
            await reader.GetByLabel("帳號", new() { Exact = true }).FillAsync("reader");
            await reader.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
            await reader.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
            await reader.Locator(".rollouts[data-interactive='true']").WaitForAsync();
            await reader.GetByRole(AriaRole.Button, new() { Name = "查看詳情" }).ClickAsync();
            await reader.GetByRole(AriaRole.Link, new() { Name = "主要主機", Exact = true }).WaitForAsync();
            Assert.Contains("主要主機", await reader.Locator("main").InnerTextAsync());
            Assert.AreEqual(0, await reader.GetByRole(AriaRole.Button, new() { Name = "開始分批更新" }).CountAsync());
            Assert.AreEqual(0, await reader.GetByRole(AriaRole.Button, new() { Name = "繼續更新" }).CountAsync());
        }
        var evidence = Environment.GetEnvironmentVariable("VIGILOOM_ROLLOUT_EVIDENCE_DIR") ?? Path.Combine(context.TestRunDirectory!, "group-rollout-ui");
        Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "desktop.png"), FullPage = true });
        await page.SetViewportSizeAsync(390, 844);
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        Assert.IsTrue(await page.GetByText("左右滑動查看更新紀錄", new() { Exact = true }).IsVisibleAsync());
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "mobile.png"), FullPage = true });
    }, context.CancellationToken);
}
