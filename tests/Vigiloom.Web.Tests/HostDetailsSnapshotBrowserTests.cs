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

    private sealed class SnapshotClient : IHostMonitoringClient
    {
        private readonly Guid snapshot = Guid.CreateVersion7();
        private readonly Guid partial = Guid.CreateVersion7();
        private readonly Guid agent = Guid.CreateVersion7();
        private readonly Guid scope = Guid.CreateVersion7();
        private readonly DateTimeOffset now = DateTimeOffset.UtcNow;
        private int inventoryReads;
        public int InventoryReads => Volatile.Read(ref inventoryReads);
        public Task<DeviceInventoryView?> GetInventoryAsync(Guid deviceId, CancellationToken token)
        {
            Interlocked.Increment(ref inventoryReads);
            return Task.FromResult<DeviceInventoryView?>(new(deviceId, agent, [new(InventorySource.HostProcesses, now, InventoryOutcome.Partial, InventoryCompletion.Partial, null, now, snapshot, now, 2, 2,
                PartialSnapshotId: partial, PartialObservedAt: now, PartialChunkCount: 1, PartialItemCount: 1, PartialReason: InventoryPartialReason.Truncated)]));
        }
        public Task<InventorySnapshotChunkView?> GetInventoryChunkAsync(Guid deviceId, InventorySource source, Guid snapshotId, int chunkIndex, CancellationToken token) => Task.FromResult<InventorySnapshotChunkView?>(
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
