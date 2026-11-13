using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using Vigiloom.Contracts.Agent;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
[DoNotParallelize]
public sealed class HostDetailsSnapshotBrowserTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public Task InventoryStates_AreDistinguishableInHostDetails() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        var client = new SnapshotClient(statusMatrix: true);
        using var factory = PingTestSite.CreateFactory(database.ConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => { services.RemoveAll<IHostMonitoringClient>(); services.AddSingleton<IHostMonitoringClient>(client); }));
        factory.UseKestrel(0); factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        var device = new Device("status server", "127.0.0.6", true, DateTimeOffset.UtcNow);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await using var gate = await AutoRenderGate.HoldAsync(page);
        var url = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        await page.GotoAsync($"{url}/devices/{device.Id:D}");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("reader");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        var host = page.Locator(".host-details[data-runtime='Server']");
        await host.WaitForAsync(new() { Timeout = 60000 });
        await host.GetByText("Windows 軟體的 32／64 位元標示是登錄檢視，並非軟體本身的架構。", new() { Exact = true }).WaitForAsync();
        await Assertions.Expect(page.Locator("#host-source option").GetByText("Windows 軟體（32 位元登錄檢視）", new() { Exact = true })).ToHaveCountAsync(1);
        await Assertions.Expect(page.Locator("#host-source option").GetByText("Windows 軟體（64 位元登錄檢視）", new() { Exact = true })).ToHaveCountAsync(1);
        await host.GetByText("Agent 結果：權限不足", new() { Exact = false }).WaitForAsync();
        await host.GetByText("retained 32-bit view", new() { Exact = true }).WaitForAsync();
        Assert.Contains($"最後成功盤點：{client.LastSuccessfulAt:yyyy-MM-dd HH:mm:ss}", await host.InnerTextAsync());
        await page.Locator("#host-source").SelectOptionAsync(((int)InventorySource.WindowsSoftware64).ToString());
        await host.GetByText("Agent 結果：不支援", new() { Exact = false }).WaitForAsync();
        await host.GetByText("retained 64-bit view", new() { Exact = true }).WaitForAsync();
        Assert.AreEqual(0, await host.GetByText("retained 32-bit view", new() { Exact = true }).CountAsync());
        await page.Locator("#host-source").SelectOptionAsync(((int)InventorySource.LinuxDpkg).ToString());
        await host.GetByText("Agent 結果：採集失敗", new() { Exact = false }).WaitForAsync();
        await page.Locator("#host-source").SelectOptionAsync(((int)InventorySource.LinuxRpm).ToString());
        await host.GetByText("Agent 結果：尚無回報", new() { Exact = false }).WaitForAsync();
        await host.GetByText("尚無可查看的快照。", new() { Exact = true }).WaitForAsync();
        await page.Locator("#host-source").SelectOptionAsync(((int)InventorySource.WindowsQuickFix).ToString());
        await host.GetByText("Agent 結果：完整", new() { Exact = false }).WaitForAsync();
        await host.GetByText("快照沒有項目。", new() { Exact = true }).WaitForAsync();
    }, TestContext.CancellationToken);

    [TestMethod]
    public Task SnapshotReplacement_DoesNotMixPages() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        var client = new SnapshotClient();
        using var factory = PingTestSite.CreateFactory(database.ConnectionString).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => { services.RemoveAll<IHostMonitoringClient>(); services.AddSingleton<IHostMonitoringClient>(client); }));
        factory.UseKestrel(0); factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        var device = new Device("snapshot server", "127.0.0.5", true, DateTimeOffset.UtcNow);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await using var gate = await AutoRenderGate.HoldAsync(page);
        var url = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        await page.GotoAsync($"{url}/devices/{device.Id:D}");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("reader");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.Locator(".host-details[data-runtime='Server']").WaitForAsync(new() { Timeout = 60000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "查看盤點變更", Exact = true }).ClickAsync();
        var inventoryChanges = page.GetByRole(AriaRole.Region, new() { Name = "盤點變更表格，可左右捲動" });
        await inventoryChanges.GetByText("部分觀察：首次看到", new() { Exact = true }).WaitForAsync();
        if (Environment.GetEnvironmentVariable("VIGILOOM_HOST_UI_EVIDENCE_DIR") is { Length: > 0 } evidence)
        {
            Directory.CreateDirectory(evidence);
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "inventory-changes.png"), FullPage = true });
        }
        await page.SetViewportSizeAsync(390, 844);
        Assert.IsTrue(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth"));
        await page.SetViewportSizeAsync(1280, 720);
        var group = inventoryChanges.GetByRole(AriaRole.Row).Filter(new() { HasText = "test group" });
        await group.GetByText("變更後", new() { Exact = true }).ClickAsync();
        await inventoryChanges.GetByText("版本：2.0", new() { Exact = false }).WaitForAsync();
        await inventoryChanges.GetByRole(AriaRole.Button, new() { Name = "查看成員（101）" }).ClickAsync();
        await inventoryChanges.GetByRole(AriaRole.Button, new() { Name = "下一頁", Exact = true }).ClickAsync();
        await inventoryChanges.GetByText("member-100", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "較早盤點變更", Exact = true }).ClickAsync();
        await inventoryChanges.GetByText("other group", new() { Exact = true }).WaitForAsync();
        await inventoryChanges.GetByText("變更後", new() { Exact = true }).ClickAsync();
        await inventoryChanges.GetByRole(AriaRole.Button, new() { Name = "查看成員（1）" }).ClickAsync();
        await inventoryChanges.GetByText("single-member", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "較新盤點變更", Exact = true }).ClickAsync();
        await inventoryChanges.GetByText("部分觀察：首次看到", new() { Exact = true }).WaitForAsync();
        await page.Locator("#inventory-change-source").SelectOptionAsync(((int)InventorySource.WindowsSoftware64).ToString());
        await page.GetByText("這段時間沒有盤點變更。", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "程序與服務", Exact = true }).ClickAsync();
        await page.GetByText("程序、作業系統服務與監聽連線是週期性快照", new() { Exact = false }).WaitForAsync();
        await page.GetByText("partial package", new() { Exact = true }).WaitForAsync();
        Assert.AreEqual(2, client.InventoryReads, "Prerender must not perform a duplicate inventory read.");
        Assert.Contains("最後成功盤點", await page.Locator(".host-details").InnerTextAsync());
        await page.Locator("#snapshot-kind").SelectOptionAsync("complete");
        await page.GetByText("original package", new() { Exact = true }).WaitForAsync();
        await page.Locator(".host-details").GetByRole(AriaRole.Button, new() { Name = "下一頁", Exact = true }).ClickAsync();
        await page.GetByText("快照已更新，請重新載入最新資料。", new() { Exact = true }).WaitForAsync();
        Assert.AreEqual(0, await page.GetByText("original package", new() { Exact = true }).CountAsync());
        await page.GetByRole(AriaRole.Button, new() { Name = "檔案完整性", Exact = true }).ClickAsync();
        await page.GetByText("變更前", new() { Exact = true }).ClickAsync();
        await page.GetByText("old-owner", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "查看基線", Exact = true }).ClickAsync();
        await page.Locator(".host-details table").Nth(1).GetByText("original/file", new() { Exact = true }).WaitForAsync();
        await page.GetByText("基線版本若與目前設定不同，原始掃描路徑可能不同，歷史路徑未提供。", new() { Exact = false }).WaitForAsync();
        await page.Locator(".host-details").GetByRole(AriaRole.Button, new() { Name = "下一頁", Exact = true }).ClickAsync();
        await page.GetByText("基線已更新，請重新載入最新資料。", new() { Exact = true }).WaitForAsync();
        Assert.AreEqual(0, await page.GetByText("original/file", new() { Exact = true }).CountAsync());
    }, TestContext.CancellationToken);

    private sealed class SnapshotClient(bool statusMatrix = false) : IHostMonitoringClient
    {
        private readonly Guid snapshot = Guid.CreateVersion7();
        private readonly Guid partial = Guid.CreateVersion7();
        private readonly Guid agent = Guid.CreateVersion7();
        private readonly Guid scope = Guid.CreateVersion7();
        private readonly DateTimeOffset now = DateTimeOffset.UtcNow;
        public DateTimeOffset LastSuccessfulAt => now.AddHours(-1);
        private int inventoryReads;
        public int InventoryReads => Volatile.Read(ref inventoryReads);
        public Task<InventoryChangePage?> GetInventoryChangesAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, InventorySource? source, string? cursor, CancellationToken token) => Task.FromResult<InventoryChangePage?>(source is not null ? new([], null) : cursor is null
            ? new([new(agent, InventorySource.LinuxGroups, now, InventoryChangeType.Modified, new InventoryEntry("one", "test group"), new InventoryEntry("one", "test group", Version: "2.0", Members: Enumerable.Range(0, 101).Select(i => $"member-{i}").ToArray())),
                new(agent, InventorySource.HostProcesses, now, InventoryChangeType.NewlyObserved, null, new InventoryEntry("process", "test process", NumericId: 5, ProcessStartedAt: now))], "next")
            : new([new(agent, InventorySource.LinuxGroups, now, InventoryChangeType.Modified, new InventoryEntry("two", "other group"), new InventoryEntry("two", "other group", Members: ["single-member"]))], null));
        public Task<DeviceInventoryView?> GetInventoryAsync(Guid deviceId, CancellationToken token)
        {
            Interlocked.Increment(ref inventoryReads);
            if (statusMatrix) return Task.FromResult<DeviceInventoryView?>(new(deviceId, agent,
            [
                new(InventorySource.WindowsSoftware32, now, InventoryOutcome.PermissionDenied, InventoryCompletion.Rejected, null, now.AddHours(-1), snapshot, now.AddHours(-1), 1, 1),
                new(InventorySource.WindowsSoftware64, now, InventoryOutcome.Unsupported, null, null, now.AddHours(-1), snapshot, now.AddHours(-1), 1, 1),
                new(InventorySource.LinuxDpkg, now, InventoryOutcome.Error, null, null, null, null, null, null, null),
                new(InventorySource.LinuxRpm, null, null, null, null, null, null, null, null, null),
                new(InventorySource.WindowsQuickFix, now, InventoryOutcome.Complete, InventoryCompletion.Complete, null, now, partial, now, 1, 0)
            ]));
            return Task.FromResult<DeviceInventoryView?>(new(deviceId, agent, [new(InventorySource.HostProcesses, now, InventoryOutcome.Partial, InventoryCompletion.Partial, null, now, snapshot, now, 2, 2,
                PartialSnapshotId: partial, PartialObservedAt: now, PartialChunkCount: 1, PartialItemCount: 1, PartialReason: InventoryPartialReason.Truncated)]));
        }
        public Task<InventorySnapshotChunkView?> GetInventoryChunkAsync(Guid deviceId, InventorySource source, Guid snapshotId, int chunkIndex, CancellationToken token) => statusMatrix
            ? Task.FromResult<InventorySnapshotChunkView?>(new(snapshotId, source, source == InventorySource.WindowsQuickFix ? now : LastSuccessfulAt, 0, 1, source == InventorySource.WindowsQuickFix ? [] : [new InventoryEntry("view", source == InventorySource.WindowsSoftware64 ? "retained 64-bit view" : "retained 32-bit view")]))
            : Task.FromResult<InventorySnapshotChunkView?>(
            chunkIndex != 0 ? null : snapshotId == partial ? new(partial, source, now, 0, 1, [new InventoryEntry("partial", "partial package", NumericId: 1, ProcessStartedAt: now)])
                : new(snapshot, source, now, 0, 2, [new InventoryEntry("one", "original package", NumericId: 2, ProcessStartedAt: now)]));
        public Task<HostMetricPage?> GetMetricsAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, AgentMetricKind? kind, CancellationToken token) => throw new NotSupportedException();
        public Task<HostMetricAggregate?> AggregateMetricsAsync(Guid deviceId, Guid agentId, DateTimeOffset from, DateTimeOffset until, AgentMetricKind kind, string source, string? dimension, int bucketSeconds, CancellationToken token) => throw new NotSupportedException();
        public Task<HostLogPage?> GetLogsAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, CancellationToken token) => throw new NotSupportedException();
        public Task<DeviceIntegrityView?> GetIntegrityAsync(Guid deviceId, CancellationToken token) => Task.FromResult<DeviceIntegrityView?>(
            new(deviceId, agent, [new(scope, 2, IntegrityScopeKind.Directory, "/etc", true, now, IntegrityScanOutcome.Complete, IntegrityCompletion.Complete, null, false, now, snapshot, 1, now, 2, 2)]));
        public Task<IntegritySnapshotChunkView?> GetIntegrityChunkAsync(Guid deviceId, Guid scopeId, Guid snapshotId, int chunkIndex, CancellationToken token) => Task.FromResult<IntegritySnapshotChunkView?>(
            chunkIndex == 0 ? new(snapshot, scope, 1, now, 0, 2, [new IntegrityItem("original/file", IntegrityItemKind.File)]) : null);
        public Task<IntegrityChangePage?> GetIntegrityChangesAsync(Guid deviceId, DateTimeOffset from, DateTimeOffset until, string? cursor, CancellationToken token) => Task.FromResult<IntegrityChangePage?>(
            new([new(agent, scope, 1, now, IntegrityChangeType.Modified, new("original/file", IntegrityItemKind.File, "old-digest", null, "old-owner"),
                new("original/file", IntegrityItemKind.File, "new-digest", null, "new-owner"), false)], null));
    }
}
