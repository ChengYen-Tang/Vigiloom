using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Modules.SecurityAnalysis.Application.Inventory;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Agent;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
[DoNotParallelize]
public sealed class AgentVersionBrowserTests(TestContext testContext)
{
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public Task AdministratorCanAssignAndCancelVersion() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        factory.UseKestrel(0);
        factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        var device = new Device("測試主機", "127.0.0.1", true, DateTimeOffset.UtcNow);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        using var scope = factory.Services.CreateScope();
        var identity = scope.ServiceProvider.GetRequiredService<AgentIdentity>();
        var issued = await identity.IssueAsync(device.Id, Guid.CreateVersion7(), token);
        var agent = await identity.RedeemAsync(issued.Secret, SHA256.HashData(RandomNumberGenerator.GetBytes(32)), token);
        Assert.IsNotNull(agent.AgentId);
        var packageId = Guid.CreateVersion7();
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
        {
            db.Packages.Add(new AgentPackage { Id = packageId, Version = "1.2.3", RuntimeIdentifier = "win-x64", Size = 1,
                Sha256 = new string('a', 64), ManifestSha256 = new string('b', 64), Listed = true, VerifiedUntil = DateTimeOffset.UtcNow.AddDays(1) });
            await db.SaveChangesAsync(token);
        }
        var url = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        if (Environment.GetEnvironmentVariable("VIGILOOM_VISUAL_QA") == "1") Assert.AreEqual("http://localhost:5043", url);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1024 } });
        var page = await context.NewPageAsync();
        await page.GotoAsync(url + $"/devices/{device.Id:D}/agent-version");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.Locator(".version-assignment[data-interactive='true']").WaitForAsync();
        Assert.AreEqual("尚無回報", await page.GetByText("心跳回報的 Agent 版本", new() { Exact = true }).Locator("..").Locator("dd").First.InnerTextAsync());
        await page.GetByRole(AriaRole.Radio, new() { Name = "1.2.3 win-x64" }).CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "指派版本", Exact = true }).ClickAsync();
        await page.GetByText("已儲存版本指派；設備是否安裝仍以實際回報為準。", new() { Exact = true }).WaitForAsync();
        await page.GetByText("Agent 目前尚未連線，指派將等待 Agent 重新連線後取得。重新整理頁面可查看連線狀態。", new() { Exact = true }).WaitForAsync();
        Assert.Contains("尚無盤點資料", await page.Locator("main").InnerTextAsync());
        var observed = DateTimeOffset.UtcNow;
        var host = new InventoryEntry("host", "test", "1.0", "X64", OperatingSystem: "Linux", ProcessArchitecture: "X64",
            Runtime: ".NET 10", AvailableProcessorCount: 2, AgentVersion: "1.1.0");
        var measured = InventoryDigest.MeasureChunk([host]);
        var inventory = new InventoryChunk(InventoryChunkCodec.SchemaVersion, Guid.CreateVersion7(), observed, Guid.CreateVersion7(), 1,
            InventorySource.HostSystem, observed, InventoryOutcome.Complete, 0, 1, 1, measured.Bytes, InventoryDigest.Combine([measured.Digest]), [host]);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>().CreateDbContextAsync(token))
        {
            await db.Database.OpenConnectionAsync(token);
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var stored = await new InventoryStageWriter().StageAsync(db.Database.GetDbConnection(), transaction.GetDbTransaction(),
                new InventoryOwner(agent.AgentId.Value, device.Id, device.AgentAuthorizationGeneration), inventory, observed, token);
            Assert.AreEqual(InventoryStageResult.Accepted, stored);
            await transaction.CommitAsync(token);
        }
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
        {
            var registered = await db.Agents.SingleAsync(x => x.Id == agent.AgentId.Value, token);
            registered.RecordRuntime(new AgentRuntimeReport("1.1.1", "win-x64", 25.5, 4096, [new(AgentModule.Metrics, observed)]), observed);
            var target = await db.UpdateTargets.SingleAsync(x => x.AgentId == agent.AgentId.Value, token);
            db.UpdateReceipts.Add(new AgentUpdateReceipt { AgentId = agent.AgentId.Value, UpdateId = target.Id,
                MessageId = Guid.CreateVersion7(), Sequence = 1, Result = AgentUpdateResultKind.Switching,
                OccurredAt = observed, ReceivedAt = observed });
            await db.SaveChangesAsync(token);
        }
        await page.GotoAsync(url + $"/devices/{device.Id:D}");
        await page.GetByText("25.5%", new() { Exact = true }).WaitForAsync();
        await page.GetByText("4,096 位元組", new() { Exact = true }).WaitForAsync();
        await page.GetByText($"資源指標：{observed.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC", new() { Exact = true }).WaitForAsync();
        await page.GotoAsync(url + $"/devices/{device.Id:D}/agent-version");
        await page.GetByText("1.1.0", new() { Exact = true }).WaitForAsync();
        await page.GetByText("1.1.1 (win-x64)", new() { Exact = true }).WaitForAsync();
        await page.GetByText("1.2.3：切換版本", new() { Exact = true }).WaitForAsync();
        Assert.AreEqual($"回報時間（UTC）：{observed.ToUniversalTime():yyyy-MM-dd HH:mm:ss}",
            await page.GetByText("心跳回報的 Agent 版本", new() { Exact = true }).Locator("..").Locator(".version-time").InnerTextAsync());
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
        {
            var target = await db.UpdateTargets.SingleAsync(x => x.AgentId == agent.AgentId.Value, token);
            db.UpdateReceipts.Add(new AgentUpdateReceipt { AgentId = agent.AgentId.Value, UpdateId = target.Id,
                MessageId = Guid.CreateVersion7(), Sequence = 2, Result = AgentUpdateResultKind.RolledBack,
                OccurredAt = observed.AddMinutes(1), ReceivedAt = observed.AddMinutes(2), Failure = AgentUpdateFailure.Interrupted });
            await db.SaveChangesAsync(token);
        }
        await page.ReloadAsync();
        await page.GetByText("1.2.3：已回復", new() { Exact = true }).WaitForAsync();
        await page.GetByText("原因：更新流程中斷", new() { Exact = true }).WaitForAsync();
        await page.GetByText($"發生時間（UTC）：{observed.AddMinutes(1).ToUniversalTime():yyyy-MM-dd HH:mm:ss}", new() { Exact = true }).WaitForAsync();
        await page.GetByText($"接收時間（UTC）：{observed.AddMinutes(2).ToUniversalTime():yyyy-MM-dd HH:mm:ss}", new() { Exact = true }).WaitForAsync();
        await using (var readerContext = await browser.NewContextAsync())
        {
            var reader = await readerContext.NewPageAsync();
            await reader.GotoAsync(url + $"/devices/{device.Id:D}/agent-version");
            await reader.GetByLabel("帳號", new() { Exact = true }).FillAsync("reader");
            await reader.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
            await reader.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
            await reader.GetByText("只有管理員可以變更版本指派。", new() { Exact = true }).WaitForAsync();
            Assert.Contains("1.2.3", await reader.Locator("main").InnerTextAsync());
            Assert.AreEqual(0, await reader.GetByRole(AriaRole.Button, new() { Name = "指派版本", Exact = true }).CountAsync());
        }
        var evidence = Environment.GetEnvironmentVariable("VIGILOOM_VERSION_EVIDENCE_DIR") ?? Path.Combine(testContext.TestRunDirectory!, "agent-version-ui");
        Directory.CreateDirectory(evidence);
        await page.GetByRole(AriaRole.Radio, new() { Name = "1.2.3 win-x64" }).CheckAsync();
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "desktop.png"), FullPage = true });
        await page.SetViewportSizeAsync(390, 844);
        await page.ReloadAsync();
        await page.Locator(".version-assignment[data-interactive='true']").WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "取消指派", Exact = true }).WaitForAsync();
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "mobile.png"), FullPage = true });
        await page.GetByRole(AriaRole.Button, new() { Name = "取消指派", Exact = true }).ClickAsync();
        await page.GetByText("已取消指派。", new() { Exact = true }).WaitForAsync();
        await page.GotoAsync(url + $"/devices/{device.Id:D}/agent");
        var downloads = page.GetByRole(AriaRole.Link, new() { Name = "Vigiloom 官方版本下載頁", Exact = true });
        await Assertions.Expect(downloads).ToHaveAttributeAsync("href", "https://github.com/ChengYen-Tang/Vigiloom/releases");
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "installer-mobile.png"), FullPage = true });
        await page.SetViewportSizeAsync(1440, 1024);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "installer-desktop.png"), FullPage = true });
        testContext.AddResultFile(Path.Combine(evidence, "desktop.png"));
        testContext.AddResultFile(Path.Combine(evidence, "mobile.png"));
    }, testContext.CancellationToken);
}
