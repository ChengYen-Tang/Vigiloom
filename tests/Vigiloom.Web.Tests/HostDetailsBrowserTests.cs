using Microsoft.EntityFrameworkCore;
using AngleSharp.Html.Parser;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.AgentManagement.Domain;
using Vigiloom.Modules.AgentManagement.Application;
using Vigiloom.Modules.AgentManagement.Application.HostMetrics;
using Vigiloom.Modules.AgentManagement.Persistence;
using Vigiloom.Contracts.Agent;
using Vigiloom.Modules.SecurityAnalysis.Application.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Persistence;
using Vigiloom.Contracts.Agent.Inventory;
using Vigiloom.Contracts.Agent.Integrity;
using Vigiloom.Modules.SecurityAnalysis.Domain.Integrity;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
public sealed class HostDetailsBrowserTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow("Input.MaximumEntries", "not-a-number")]
    [DataRow("Input.MaximumEntries", "2147483648")]
    [DataRow("Input.Kind", "invalid-kind")]
    [DataRow("Input.RegistryView", "invalid-view")]
    public Task IntegrityScopes_RejectsMalformedFormInput(string field, string badValue) => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        var device = new Device("Test server", "127.0.0.4", true, DateTimeOffset.UtcNow);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        using var client = PingTestSite.Client(factory);
        await PingTestSite.LoginAsync(client, "admin", token);
        var path = $"/devices/{device.Id:D}/integrity-scopes";
        using var response = await client.GetAsync(path, token);
        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token);
        var form = document.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "integrity-save");
        var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
        fields["Input.Kind"] = "Directory";
        fields["Input.Root"] = "/etc/vigiloom-test";
        fields["Input.RegistryView"] = "Registry64";
        fields["Input.ScanIntervalSeconds"] = "3600";
        fields["Input.MaximumEntries"] = "10000";
        fields["Input.MaximumFileBytes"] = "104857600";
        fields["Input.MaximumScanBytes"] = "1073741824";
        fields["Input.MaximumDepth"] = "16";
        fields["Input.Enabled"] = "true";
        fields[field] = badValue;
        using var posted = await client.PostAsync(path, new FormUrlEncodedContent(fields), token);
        Assert.AreEqual(HttpStatusCode.OK, posted.StatusCode);
        var resultDocument = await new HtmlParser().ParseDocumentAsync(await posted.Content.ReadAsStringAsync(token), token);
        Assert.Contains("請確認數字及選擇欄位", resultDocument.QuerySelector("[role=alert]")?.TextContent ?? "");
        await using var scope = factory.Services.CreateAsyncScope();
        var saved = await scope.ServiceProvider.GetRequiredService<IntegrityConfiguration>().ListAsync(device.Id, token);
        Assert.IsNotNull(saved);
        Assert.IsEmpty(saved);
    }, TestContext.CancellationToken);

    [TestMethod]
    public Task HostDetails_ReaderCanInspectButCannotManageIntegrity() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        factory.UseKestrel(0); factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        var device = new Device("Test server", "127.0.0.2", true, DateTimeOffset.UtcNow);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        var agent = new Vigiloom.Modules.AgentManagement.Domain.Agent(device.Id, 1, new byte[32], now);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
        {
            db.Agents.Add(agent);
            db.HostMetrics.Add(new HostMetric(agent.Id, Guid.CreateVersion7(), 0,
                new AgentMetricPoint(Guid.CreateVersion7(), now, AgentMetricKind.CpuTotalPercent, "test.cpu", null, 42), now));
            db.HostMetrics.Add(new HostMetric(agent.Id, Guid.CreateVersion7(), 0,
                new AgentMetricPoint(Guid.CreateVersion7(), now, AgentMetricKind.CollectionUnavailable, AgentMetricUnavailableSource.PermissionDenied, "memory", 1), now));
            db.HostMetrics.Add(new HostMetric(agent.Id, Guid.CreateVersion7(), 0,
                new AgentMetricPoint(Guid.CreateVersion7(), now.AddMinutes(-20), AgentMetricKind.CpuTotalPercent, "test.cpu", null, 20), now));
            db.HostMetrics.Add(new HostMetric(agent.Id, Guid.CreateVersion7(), 0,
                new AgentMetricPoint(Guid.CreateVersion7(), now.AddDays(-2), AgentMetricKind.CpuTotalPercent, "test.cpu", null, 67), now));
            db.HostMetrics.Add(new HostMetric(agent.Id, Guid.CreateVersion7(), 0,
                new AgentMetricPoint(Guid.CreateVersion7(), now.AddDays(-10), AgentMetricKind.CpuTotalPercent, "test.cpu", null, 17), now));
            db.HostMetrics.Add(new HostMetric(agent.Id, Guid.CreateVersion7(), 0,
                new AgentMetricPoint(Guid.CreateVersion7(), now.AddDays(-200), AgentMetricKind.CpuTotalPercent, "test.cpu", null, 13), now));
            var records = Enumerable.Range(0, 26).Select(i => new AgentLogRecord(Guid.CreateVersion7(), now, $"event-{i}", null, null, null, null, null)).ToArray();
            var batch = new AgentLogBatch(3, Guid.CreateVersion7(), now, Guid.CreateVersion7(), AgentLogSourceKind.Journal, "system", 1, records);
            for (var i = 0; i < records.Length; i++) db.HostLogs.Add(new HostLog(agent.Id, batch, i, now));
            await db.SaveChangesAsync(token);
        }
        var agentContexts = factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>();
        Assert.AreEqual(6, await new HostMetricRollupBackfill(agentContexts).RunBatchAsync(token));
        await using (var db = await agentContexts.CreateDbContextAsync(token))
            await db.HostMetrics.Where(x => x.AgentId == agent.Id && x.OccurredAt < now.AddDays(-100)).ExecuteDeleteAsync(token);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var thresholds = scope.ServiceProvider.GetRequiredService<HostMetricThresholds>();
            Assert.IsNotNull(await thresholds.UpdateAsync(device.Id, agent.Id, AgentMetricKind.CpuTotalPercent, "test.cpu", null,
                new(true, HostMetricComparison.AtLeast, 42, 300, ""), Guid.CreateVersion7(), token));
        }
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<SecurityAnalysisDbContext>>().CreateDbContextAsync(token))
        {
            var before = new InventoryEntry("package", "Example package", "1.0");
            var after = before with { Version = "2.0" };
            var beforeJson = System.Text.Json.JsonSerializer.Serialize(before);
            var afterJson = System.Text.Json.JsonSerializer.Serialize(after);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO [security].[InventoryChanges] ([AgentId], [DeviceId], [Source], [Generation], [ObservedAt], [RecordedAt], [Kind], [Key], [BeforeJson], [AfterJson]) VALUES ({agent.Id}, {device.Id}, {(int)InventorySource.LinuxDpkg}, {1L}, {now}, {now}, {(int)InventoryChangeType.Modified}, {before.Key}, {beforeJson}, {afterJson})", token);
            var item = new IntegrityItem(IntegrityKey.Create(IntegrityItemKind.File, ["private-config"]), IntegrityItemKind.File, new string('a', 64));
            db.IntegrityChanges.Add(new IntegrityChangeRecord { AgentId = agent.Id, DeviceId = device.Id,
                ScopeId = Guid.CreateVersion7(), ScopeRevision = 1, Generation = 1, ObservedAt = now, RecordedAt = now,
                Kind = (int)IntegrityChangeType.Added, Key = item.Key, AfterJson = System.Text.Json.JsonSerializer.Serialize(item) });
            await db.SaveChangesAsync(token);
        }
        var url = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1024 } });
        var page = await context.NewPageAsync();
        await using var wasmGate = await AutoRenderGate.HoldAsync(page);
        await page.GotoAsync($"{url}/devices/{device.Id:D}");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("reader");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.Locator(".host-details[data-runtime='Server']").WaitForAsync(new() { Timeout = 60000 });
        await wasmGate.ReleaseAsync();
        await page.GetByText("目前沒有這類盤點資料。", new() { Exact = true }).WaitForAsync(new() { Timeout = 60000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "查看盤點變更", Exact = true }).ClickAsync();
        var changes = page.GetByRole(AriaRole.Region, new() { Name = "盤點變更表格，可左右捲動" });
        await changes.GetByText("Example package", new() { Exact = true }).WaitForAsync();
        await changes.GetByText("變更前", new() { Exact = true }).ClickAsync();
        await changes.GetByText("版本：1.0", new() { Exact = false }).WaitForAsync();
        if (Environment.GetEnvironmentVariable("VIGILOOM_HOST_UI_EVIDENCE_DIR") is { Length: > 0 } evidence)
        {
            Directory.CreateDirectory(evidence);
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "device-details.png") });
        }
        await page.GetByRole(AriaRole.Button, new() { Name = "效能", Exact = true }).ClickAsync();
        await page.GetByText("memory：權限不足", new() { Exact = false }).WaitForAsync();
        await page.GetByText("已達閾值", new() { Exact = true }).WaitForAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var thresholds = scope.ServiceProvider.GetRequiredService<HostMetricThresholds>();
            var current = await thresholds.GetAsync(device.Id, agent.Id, AgentMetricKind.CpuTotalPercent, "test.cpu", null, token);
            Assert.IsNotNull(current?.Threshold);
            Assert.IsNotNull(await thresholds.UpdateAsync(device.Id, agent.Id, AgentMetricKind.CpuTotalPercent, "test.cpu", null,
                new(true, HostMetricComparison.AtLeast, 43, 300, current.Threshold.Version), Guid.CreateVersion7(), token));
        }
        await page.GetByRole(AriaRole.Button, new() { Name = "更新評估", Exact = true }).ClickAsync();
        await page.GetByText("未達觸發條件", new() { Exact = true }).WaitForAsync();
        Assert.AreEqual(0, await page.GetByRole(AriaRole.Link, new() { Name = "設定此來源的閾值" }).CountAsync());
        await page.GetByText("以表格查看資料", new() { Exact = true }).ClickAsync();
        await page.Locator(".time-series-chart table").GetByText("42", new() { Exact = true }).WaitForAsync();
        await page.Locator(".time-series-chart table").GetByText("無資料", new() { Exact = true }).WaitForAsync();
        Assert.AreEqual(0, await page.Locator(".time-series-chart table").GetByText("67", new() { Exact = true }).CountAsync());
        await page.Locator("#host-range").SelectOptionAsync("168");
        await page.Locator(".time-series-chart table").GetByText("67", new() { Exact = true }).WaitForAsync(new() { State = WaitForSelectorState.Attached });
        Assert.AreEqual(0, await page.Locator(".time-series-chart table").GetByText("17", new() { Exact = true }).CountAsync());
        await page.Locator("#host-range").SelectOptionAsync("720");
        await page.Locator(".time-series-chart table").GetByText("17", new() { Exact = true }).WaitForAsync(new() { State = WaitForSelectorState.Attached });
        await page.Locator("#host-range").SelectOptionAsync("8760");
        await page.Locator(".time-series-chart table").GetByText("13", new() { Exact = true }).WaitForAsync(new() { State = WaitForSelectorState.Attached });
        if (Environment.GetEnvironmentVariable("VIGILOOM_HOST_UI_EVIDENCE_DIR") is { Length: > 0 } metricEvidence)
        {
            Directory.CreateDirectory(metricEvidence);
            await page.ScreenshotAsync(new() { Path = Path.Combine(metricEvidence, "metric-year-desktop.png"), FullPage = true });
        }
        await page.SetViewportSizeAsync(390, 844);
        await page.WaitForFunctionAsync("() => document.documentElement.scrollWidth <= window.innerWidth", null, new() { Timeout = 10000 });
        Assert.IsLessThanOrEqualTo(390, await page.EvaluateAsync<int>("() => document.documentElement.scrollWidth"));
        if (Environment.GetEnvironmentVariable("VIGILOOM_HOST_UI_EVIDENCE_DIR") is { Length: > 0 } mobileEvidence)
            await page.ScreenshotAsync(new() { Path = Path.Combine(mobileEvidence, "metric-year-mobile.png"), FullPage = true });
        await page.SetViewportSizeAsync(1440, 1024);
        string shortenedVersion;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var retention = scope.ServiceProvider.GetRequiredService<HostMetricRetentionSettings>();
            var current = await retention.GetAsync(token);
            var actor = Guid.CreateVersion7();
            var preview = await retention.PreviewAsync(new(30, 60, current.Version), actor, token);
            Assert.IsNotNull(preview);
            var saved = await retention.UpdateAsync(new(30, 60, current.Version, preview.ConfirmationToken), actor, token);
            Assert.IsNotNull(saved);
            shortenedVersion = saved.Version;
        }
        await page.Locator("#host-range").SelectOptionAsync("720");
        await page.GetByText("原始樣本保存 30 天，五分鐘彙總保存 60 天。", new() { Exact = true }).WaitForAsync();
        Assert.AreEqual(0, await page.Locator("#host-range option[value='8760']").CountAsync());
        await page.Locator("#host-range").SelectOptionAsync("1440");
        await page.Locator(".time-series-chart table").GetByText("17", new() { Exact = true }).WaitForAsync(new() { State = WaitForSelectorState.Attached });
        await using (var scope = factory.Services.CreateAsyncScope())
            Assert.IsNotNull(await scope.ServiceProvider.GetRequiredService<HostMetricRetentionSettings>().UpdateAsync(new(30, 365, shortenedVersion, null), Guid.CreateVersion7(), token));
        await page.Locator("#metric-kind").SelectOptionAsync(((int)AgentMetricKind.SwapTotalBytes).ToString());
        await page.GetByText("這段時間沒有此指標。", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "主機日誌", Exact = true }).ClickAsync();
        await page.GetByText("event-25", new() { Exact = true }).WaitForAsync();
        await using (var adminContext = await browser.NewContextAsync())
        {
            var admin = await adminContext.NewPageAsync();
            await admin.GotoAsync($"{url}/account/login");
            await admin.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
            await admin.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
            await admin.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
            var auditUrl = $"/api/v1/operations/audit/events?from={Uri.EscapeDataString(now.AddHours(-1).ToString("O"))}&to={Uri.EscapeDataString(now.AddHours(1).ToString("O"))}";
            var json = await admin.EvaluateAsync<string>("async url => { const response = await fetch(url); if (!response.ok) throw new Error(`Audit request failed: ${response.status}`); return await response.text(); }", auditUrl);
            var audits = System.Text.Json.JsonSerializer.Deserialize<Vigiloom.Contracts.Operations.OperationAuditPage>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            Assert.IsNotNull(audits);
            Assert.IsTrue(audits.Items.Any(x => x.EventEvidence is { Kind: "host-log-list" } e && e.DeviceId == device.Id));
        }
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
            await db.HostLogs.Where(x => x.AgentId == agent.Id && x.Ordinal == 0).ExecuteDeleteAsync(token);
        await page.GetByRole(AriaRole.Button, new() { Name = "較早紀錄", Exact = true }).ClickAsync();
        await page.GetByText("這段時間沒有主機日誌。", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "較新紀錄", Exact = true }).ClickAsync();
        await page.GetByText("event-25", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "檔案完整性", Exact = true }).ClickAsync();
        await page.GetByText("尚未設定完整性監控範圍。", new() { Exact = true }).WaitForAsync();
        await page.GetByText(IntegrityKey.Create(IntegrityItemKind.File, ["private-config"]), new() { Exact = true }).WaitForAsync();
        Assert.AreEqual(0, await page.GetByRole(AriaRole.Link, new() { Name = "管理完整性監控" }).CountAsync());
        await AutoRenderGate.WaitForWebAssemblyCachedAsync(page);
        await page.ReloadAsync();
        await page.Locator(".host-details[data-runtime='WebAssembly']").WaitForAsync(new() { Timeout = 60000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "查看盤點變更", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Region, new() { Name = "盤點變更表格，可左右捲動" }).GetByText("Example package", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "效能", Exact = true }).ClickAsync();
        await page.GetByText("未達觸發條件", new() { Exact = true }).WaitForAsync();
        await page.Locator("#host-range").SelectOptionAsync("8760");
        await page.GetByText("以表格查看資料", new() { Exact = true }).ClickAsync();
        await page.Locator(".time-series-chart table").GetByText("13", new() { Exact = true }).WaitForAsync(new() { State = WaitForSelectorState.Attached });
        Assert.AreEqual(0, await page.GetByRole(AriaRole.Link, new() { Name = "設定此來源的閾值" }).CountAsync());
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var thresholds = scope.ServiceProvider.GetRequiredService<HostMetricThresholds>();
            var current = await thresholds.GetAsync(device.Id, agent.Id, AgentMetricKind.CpuTotalPercent, "test.cpu", null, token);
            Assert.IsNotNull(current?.Threshold);
            Assert.IsNotNull(await thresholds.UpdateAsync(device.Id, agent.Id, AgentMetricKind.CpuTotalPercent, "test.cpu", null,
                new(true, HostMetricComparison.AtLeast, 42, 30, current.Threshold.Version), Guid.CreateVersion7(), token));
        }
        await page.GetByRole(AriaRole.Button, new() { Name = "更新評估", Exact = true }).ClickAsync();
        await page.GetByText("未知：樣本已過期", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "主機日誌", Exact = true }).ClickAsync();
        await page.GetByText("event-25", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "檔案完整性", Exact = true }).ClickAsync();
        await page.GetByText(IntegrityKey.Create(IntegrityItemKind.File, ["private-config"]), new() { Exact = true }).WaitForAsync();
        await page.GotoAsync($"{url}/devices/{device.Id:D}?tab=vulnerabilities&search=CVE-2025-1234");
        await page.Locator(".host-tabs button.active").GetByText("漏洞", new() { Exact = true }).WaitForAsync();
        await page.GetByText("尚無評估結果", new() { Exact = false }).First.WaitForAsync();
        var thresholdUrl = $"{url}/devices/{device.Id:D}/metric-threshold?agentId={agent.Id:D}&kind=1&source=test.cpu";
        await page.GotoAsync(thresholdUrl);
        Assert.AreNotEqual(thresholdUrl, page.Url);
        Assert.AreEqual(0, await page.GetByLabel("閾值（%）").CountAsync());
        var response = await page.GotoAsync($"{url}/devices/{device.Id:D}/integrity-scopes");
        Assert.IsNotNull(response);
        Assert.AreNotEqual($"{url}/devices/{device.Id:D}/integrity-scopes", page.Url);
        Assert.AreEqual(0, await page.GetByLabel("完整路徑").CountAsync());
    }, TestContext.CancellationToken);

    [TestMethod]
    [DataRow("Input.Value", "not-a-number")]
    [DataRow("Input.FreshnessSeconds", "2147483648")]
    [DataRow("Input.Comparison", "invalid")]
    public Task HostMetricThreshold_RejectsMalformedForm(string field, string invalidValue) => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        var device = new Device("Threshold form", "127.0.0.8", true, DateTimeOffset.UtcNow);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var agent = new Vigiloom.Modules.AgentManagement.Domain.Agent(device.Id, 1, new byte[32], DateTimeOffset.UtcNow);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
        { db.Agents.Add(agent); await db.SaveChangesAsync(token); }
        using var client = PingTestSite.Client(factory); await PingTestSite.LoginAsync(client, "admin", token);
        var path = $"/devices/{device.Id:D}/metric-threshold?agentId={agent.Id:D}&kind=1&source=test.cpu";
        if (field == "Input.Value")
        {
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
            using (var english = await client.GetAsync(path, token))
            {
                var text = (await new HtmlParser().ParseDocumentAsync(await english.Content.ReadAsStringAsync(token), token)).Body!.TextContent;
                Assert.Contains("Configure performance threshold", text);
                Assert.Contains("CPU usage", text);
                Assert.Contains("test.cpu", text);
            }
            client.DefaultRequestHeaders.AcceptLanguage.Clear();
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-Hans");
            using (var simplified = await client.GetAsync(path, token))
            {
                var text = (await new HtmlParser().ParseDocumentAsync(await simplified.Content.ReadAsStringAsync(token), token)).Body!.TextContent;
                Assert.Contains("设置性能阈值", text);
            }
            client.DefaultRequestHeaders.AcceptLanguage.Clear();
        }
        using var response = await client.GetAsync(path, token);
        var document = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync(token), token);
        var form = document.QuerySelectorAll("form").Single(x => x.QuerySelector("input[name='_handler']")?.GetAttribute("value") == "metric-threshold-edit");
        var fields = form.QuerySelectorAll("input[type=hidden][name]").ToDictionary(x => x.GetAttribute("name")!, x => x.GetAttribute("value") ?? "");
        fields["Input.Value"] = "42"; fields["Input.FreshnessSeconds"] = "300"; fields["Input.Comparison"] = "AtLeast"; fields["Input.Enabled"] = "true";
        fields[field] = invalidValue;
        using var posted = await client.PostAsync(path, new FormUrlEncodedContent(fields), token);
        Assert.AreEqual(HttpStatusCode.OK, posted.StatusCode);
        var result = await new HtmlParser().ParseDocumentAsync(await posted.Content.ReadAsStringAsync(token), token);
        Assert.Contains("設定無效", result.QuerySelector("[role=alert]")?.TextContent ?? "");
        await using var check = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token);
        Assert.AreEqual(0, await check.HostMetricThresholds.CountAsync(token));
    }, TestContext.CancellationToken);

    [TestMethod]
    public Task HostMetrics_AdministratorCanConfigureThreshold() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        factory.UseKestrel(0); factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        var now = DateTimeOffset.UtcNow.AddSeconds(-2);
        var device = new Device("Threshold server", "127.0.0.7", true, now);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var agent = new Vigiloom.Modules.AgentManagement.Domain.Agent(device.Id, 1, new byte[32], now);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AgentManagementDbContext>>().CreateDbContextAsync(token))
        {
            db.Agents.Add(agent);
            db.HostMetrics.Add(new HostMetric(agent.Id, Guid.CreateVersion7(), 0,
                new AgentMetricPoint(Guid.CreateVersion7(), now, AgentMetricKind.CpuTotalPercent, "test.cpu", null, 42), now));
            await db.SaveChangesAsync(token);
        }
        var url = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var browserContext = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1024 } });
        var page = await browserContext.NewPageAsync();
        await using var wasmGate = await AutoRenderGate.HoldAsync(page);
        await page.GotoAsync($"{url}/devices/{device.Id:D}");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.Locator(".host-details[data-runtime='Server']").WaitForAsync(new() { Timeout = 60000 });
        await wasmGate.ReleaseAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "效能", Exact = true }).ClickAsync();
        await page.GetByText("未設定閾值", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "設定此來源的閾值" }).ClickAsync();
        await page.GetByLabel("閾值（%）").FillAsync("40");
        await page.GetByRole(AriaRole.Button, new() { Name = "儲存設定" }).ClickAsync();
        await page.Locator(".host-details").WaitForAsync(new() { Timeout = 60000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "效能", Exact = true }).ClickAsync();
        await page.GetByText("已達閾值", new() { Exact = true }).WaitForAsync();
        if (Environment.GetEnvironmentVariable("VIGILOOM_HOST_UI_EVIDENCE_DIR") is { Length: > 0 } evidence)
        {
            Directory.CreateDirectory(evidence);
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "metric-status.png"), FullPage = true });
            await page.SetViewportSizeAsync(390, 844);
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "metric-status-narrow.png"), FullPage = true });
            await page.SetViewportSizeAsync(1440, 1024);
        }
        await page.GetByRole(AriaRole.Link, new() { Name = "設定此來源的閾值" }).ClickAsync();
        if (Environment.GetEnvironmentVariable("VIGILOOM_HOST_UI_EVIDENCE_DIR") is { Length: > 0 } formEvidence)
        {
            Directory.CreateDirectory(formEvidence);
            await page.ScreenshotAsync(new() { Path = Path.Combine(formEvidence, "metric-threshold-form.png"), FullPage = true });
        }
        await Assertions.Expect(page.GetByLabel("閾值（%）")).ToHaveValueAsync("40");
        await page.GetByLabel("啟用閾值判斷").UncheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "儲存設定" }).ClickAsync();
        await page.Locator(".host-details").WaitForAsync(new() { Timeout = 60000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "效能", Exact = true }).ClickAsync();
        await page.GetByText("閾值已停用", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "設定此來源的閾值" }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("啟用閾值判斷")).Not.ToBeCheckedAsync();
    }, TestContext.CancellationToken);

    [TestMethod]
    public Task IntegrityScopes_AdministratorCanCreateAndEdit() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        factory.UseKestrel(0); factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        var device = new Device("Test server", "127.0.0.3", true, DateTimeOffset.UtcNow);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        { db.Devices.Add(device); await db.SaveChangesAsync(token); }
        var url = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{url}/devices/{device.Id:D}/integrity-scopes");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.GetByLabel("完整路徑").FillAsync("/etc/vigiloom-test");
        await page.GetByRole(AriaRole.Button, new() { Name = "儲存設定" }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "/etc/vigiloom-test", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "確認移除" }).ClickAsync();
        Assert.AreEqual("/etc/vigiloom-test", await page.GetByLabel("完整路徑").InputValueAsync());
        await page.GetByLabel("掃描間隔（秒，60–604800）").FillAsync("7200");
        await page.GetByRole(AriaRole.Button, new() { Name = "儲存設定" }).ClickAsync();
        Assert.AreEqual($"{url}/devices/{device.Id:D}/integrity-scopes", page.Url, await page.Locator("main").InnerTextAsync());
        await page.GetByRole(AriaRole.Link, new() { Name = "/etc/vigiloom-test", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("掃描間隔（秒，60–604800）")).ToHaveValueAsync("7200");
        await page.GetByLabel("啟用", new() { Exact = true }).UncheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "儲存設定" }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = "/etc/vigiloom-test", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByLabel("啟用", new() { Exact = true })).Not.ToBeCheckedAsync();
        await page.GetByLabel("我了解以上影響").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "確認移除" }).ClickAsync();
        await page.GetByText("尚未設定監控範圍。", new() { Exact = true }).WaitForAsync();
    }, TestContext.CancellationToken);
}
