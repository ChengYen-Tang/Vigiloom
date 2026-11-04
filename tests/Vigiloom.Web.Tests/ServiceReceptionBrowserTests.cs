using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Testing.SqlServer;
using Vigiloom.Web.Telemetry;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
[DoNotParallelize]
public sealed class ServiceReceptionBrowserTests(TestContext context)
{
    [TestMethod]
    [Timeout(240000, CooperativeCancellation = true)]
    public Task ReceptionPage_DesktopAndMobile() => SqlTestDatabase.ExecuteAsync(TestDatabaseServer.FromEnvironment(), async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        factory.UseKestrel(0); factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
            await db.Database.MigrateAsync(token);
        await using var scope = factory.Services.CreateAsyncScope();
        var administration = scope.ServiceProvider.GetRequiredService<SourceAdministration>();
        Guid monitoringServiceId = default;
        Guid monitoringSourceId = default;
        Guid secondMonitoringServiceId = default;
        foreach (var name in new[] { "demo-collector", "dc-west", "dc-analytics", "test-agent-01", "dev-collector" })
        {
            var source = await administration.IssueAsync(name, Guid.CreateVersion7(), token);
            if (name is "dc-west" or "test-agent-01") await administration.RevokeAsync(source.SourceId, Guid.CreateVersion7(), token);
            if (name == "demo-collector")
            {
                var discovery = scope.ServiceProvider.GetRequiredService<CandidateDiscovery>();
                await discovery.ObserveAsync(source.SourceId, "vigiloom-collector", "monitoring", "Collector", token);
                var candidate = (await discovery.ListAsync(1, token)).Single(x => x.SourceId == source.SourceId);
                var approved = await scope.ServiceProvider.GetRequiredService<ServiceApproval>().ApproveAsync(candidate.Id, Guid.CreateVersion7(), token);
                monitoringServiceId = approved.ServiceId!.Value;
                monitoringSourceId = source.SourceId;
                await discovery.ObserveAsync(source.SourceId, "second-collector", "monitoring", "Collector", token);
                var second = (await discovery.ListAsync(1, token)).Single(x => x.ServiceKey == "second-collector");
                secondMonitoringServiceId = (await scope.ServiceProvider.GetRequiredService<ServiceApproval>().ApproveAsync(second.Id, Guid.CreateVersion7(), token)).ServiceId!.Value;
            }
        }
        var metricFactory = factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
        var ingest = new MetricIngestion(metricFactory);
        foreach (var (instance, metricName) in new[] { ("collector-node-a", "otelcol_process_uptime"), ("collector-node-b", "another_metric"), ("collector-node-a", "otelcol_exporter_sent_log_records") })
        {
            var resource = new OpenTelemetry.Proto.Metrics.V1.ResourceMetrics { Resource = new OpenTelemetry.Proto.Resource.V1.Resource() };
            resource.Resource.Attributes.Add(new OpenTelemetry.Proto.Common.V1.KeyValue { Key = "service.name", Value = new OpenTelemetry.Proto.Common.V1.AnyValue { StringValue = "vigiloom-collector" } });
            resource.Resource.Attributes.Add(new OpenTelemetry.Proto.Common.V1.KeyValue { Key = "deployment.environment.name", Value = new OpenTelemetry.Proto.Common.V1.AnyValue { StringValue = "monitoring" } });
            resource.Resource.Attributes.Add(new OpenTelemetry.Proto.Common.V1.KeyValue { Key = "service.instance.id", Value = new OpenTelemetry.Proto.Common.V1.AnyValue { StringValue = instance } });
            var sample = new OpenTelemetry.Proto.Metrics.V1.NumberDataPoint { TimeUnixNano = (ulong)DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds() * 1_000_000, AsDouble = 1 };
            if (metricName == "otelcol_exporter_sent_log_records") sample.Attributes.Add(new OpenTelemetry.Proto.Common.V1.KeyValue { Key = "exporter", Value = new OpenTelemetry.Proto.Common.V1.AnyValue { StringValue = "otlp_http/source-a" } });
            var metric = new OpenTelemetry.Proto.Metrics.V1.Metric { Name = metricName, Sum = new OpenTelemetry.Proto.Metrics.V1.Sum { IsMonotonic = true, AggregationTemporality = OpenTelemetry.Proto.Metrics.V1.AggregationTemporality.Cumulative, DataPoints = { sample } } };
            Assert.AreEqual(MetricIngestionStatus.Accepted, await ingest.WriteAsync(monitoringSourceId, resource, new OpenTelemetry.Proto.Metrics.V1.ScopeMetrics(), metric, token));
        }
        {
            var resource = new OpenTelemetry.Proto.Metrics.V1.ResourceMetrics { Resource = new OpenTelemetry.Proto.Resource.V1.Resource() };
            resource.Resource.Attributes.Add(new OpenTelemetry.Proto.Common.V1.KeyValue { Key = "service.name", Value = new OpenTelemetry.Proto.Common.V1.AnyValue { StringValue = "vigiloom-collector" } });
            resource.Resource.Attributes.Add(new OpenTelemetry.Proto.Common.V1.KeyValue { Key = "deployment.environment.name", Value = new OpenTelemetry.Proto.Common.V1.AnyValue { StringValue = "monitoring" } });
            resource.Resource.Attributes.Add(new OpenTelemetry.Proto.Common.V1.KeyValue { Key = "service.instance.id", Value = new OpenTelemetry.Proto.Common.V1.AnyValue { StringValue = "collector-node-a" } });
            var started = DateTimeOffset.UtcNow;
            foreach (var (offset, value) in new[] { (1, 1L), (2, 2L) })
            {
                var point = new OpenTelemetry.Proto.Metrics.V1.NumberDataPoint { StartTimeUnixNano = (ulong)started.ToUnixTimeMilliseconds() * 1_000_000,
                    TimeUnixNano = (ulong)started.AddSeconds(offset).ToUnixTimeMilliseconds() * 1_000_000, AsInt = value };
                point.Attributes.Add(new OpenTelemetry.Proto.Common.V1.KeyValue { Key = "exporter", Value = new OpenTelemetry.Proto.Common.V1.AnyValue { StringValue = "otlp_http/source-a" } });
                var metric = new OpenTelemetry.Proto.Metrics.V1.Metric { Name = "otelcol_exporter_send_failed_log_records", Sum = new OpenTelemetry.Proto.Metrics.V1.Sum {
                    IsMonotonic = true, AggregationTemporality = OpenTelemetry.Proto.Metrics.V1.AggregationTemporality.Cumulative, DataPoints = { point } } };
                Assert.AreEqual(MetricIngestionStatus.Accepted, await ingest.WriteAsync(monitoringSourceId, resource, new OpenTelemetry.Proto.Metrics.V1.ScopeMetrics(), metric, token));
            }
        }
        var rejections = factory.Services.GetRequiredService<ReceptionRejectionDiagnostics>();
        rejections.Record(ReceptionRejectionKind.CredentialRejected, ReceptionIngress.Discovery);
        rejections.Record(ReceptionRejectionKind.ServiceNotAuthorized, ReceptionIngress.Otlp, monitoringSourceId);
        await rejections.FlushAsync(token);
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var browserContext = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1024 } });
        var page = await browserContext.NewPageAsync();
        var address = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        await page.GotoAsync(address + "/settings/service-reception");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "管線接入", Exact = true }).WaitForAsync();
        Assert.Contains("Collector 健康狀態：未知", await page.Locator("main").InnerTextAsync());
        Assert.Contains("代表來源：demo-collector", await page.Locator("main").InnerTextAsync());
        await page.GetByText("如何解讀這些數字？").ClickAsync();
        Assert.Contains("總次數包含所有來源", await page.Locator("main").InnerTextAsync());
        await page.GetByText("如何解讀這些數字？").ClickAsync();
        Assert.IsTrue(await page.GetByRole(AriaRole.Link, new() { Name = "下載 Collector 設定範例" }).IsVisibleAsync());
        Assert.IsTrue(await page.GetByRole(AriaRole.Link, new() { Name = "下載應用程式 SDK 設定範例" }).IsVisibleAsync());
        var evidence = Environment.GetEnvironmentVariable("VIGILOOM_RECEPTION_EVIDENCE_DIR") ?? Path.Combine(context.TestRunDirectory!, "service-reception-ui");
        Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "desktop.png"), FullPage = true });
        await page.SetViewportSizeAsync(390, 844);
        await page.ReloadAsync();
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        Assert.IsTrue(await page.GetByText("表格可左右滑動，查看完整接收時間。").IsVisibleAsync());
        Assert.IsTrue(await page.GetByRole(AriaRole.Link, new() { Name = "下載 Collector 設定範例" }).IsVisibleAsync());
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "mobile.png"), FullPage = true });
        await page.GetByText("設定範例需要哪些資訊？").ClickAsync();
        Assert.IsTrue(await page.GetByText("Collector 自身監控另需第三個專用來源憑證", new() { Exact = false }).IsVisibleAsync());
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "mobile-guide.png"), FullPage = true });
        var region = page.GetByRole(AriaRole.Region, new() { Name = "來源接收概覽，可左右滑動" });
        await region.FocusAsync();
        Assert.IsTrue(await region.EvaluateAsync<bool>("e => document.activeElement === e"));
        await page.GetByRole(AriaRole.Link, new() { Name = "查看 Collector 自身回報" }).ClickAsync();
        await page.Locator(".service-choice").First.WaitForAsync();
        var options = await page.Locator(".service-choice").AllTextContentsAsync();
        Assert.IsTrue(options.Any(x => x.Contains(monitoringServiceId.ToString(), StringComparison.Ordinal)), string.Join(" | ", options));
        Assert.IsTrue(options.Any(x => x.Contains(secondMonitoringServiceId.ToString(), StringComparison.Ordinal)), string.Join(" | ", options));
        await page.Locator($"input[name='service'][value='{monitoringServiceId:D}']").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "查看回報" }).ClickAsync();
        Assert.IsTrue(await page.GetByRole(AriaRole.Heading, new() { Name = "執行個體回報" }).IsVisibleAsync());
        Assert.Contains("collector-node-a", await page.Locator("main").InnerTextAsync());
        Assert.Contains("尚無資料", await page.Locator("main").InnerTextAsync());
        await page.GetByText("登記此 Collector", new() { Exact = true }).ClickAsync();
        await page.GetByLabel("Collector 名稱", new() { Exact = true }).FillAsync("Demo Collector");
        await page.GetByLabel("我已確認此服務使用僅供 Collector 自身監控的來源憑證").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "登記 Collector", Exact = true }).ClickAsync();
        await page.GetByText("Collector 已登記。", new() { Exact = true }).WaitForAsync();
        Assert.Contains("Demo Collector", await page.Locator("main").InnerTextAsync());
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        var reportRegion = page.GetByRole(AriaRole.Region, new() { Name = "Collector 執行個體回報，可左右滑動" });
        await reportRegion.FocusAsync();
        await reportRegion.PressAsync("ArrowRight");
        await page.WaitForFunctionAsync("() => document.querySelector('[aria-label=\"Collector 執行個體回報，可左右滑動\"]').scrollLeft > 0");
        Assert.IsGreaterThan(0, await reportRegion.EvaluateAsync<int>("e => e.scrollLeft"));
        await reportRegion.EvaluateAsync("e => { e.scrollLeft = 0; }");
        await reportRegion.EvaluateAsync("e => e.blur()");
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "collector-reports-mobile.png"), FullPage = true });
        await page.SetViewportSizeAsync(1440, 1024);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "collector-reports-desktop.png"), FullPage = true });
        await page.GetByRole(AriaRole.Link, new() { Name = "管理來源對應" }).ClickAsync();
        var sourceChoice = page.Locator(".source-choice").Filter(new() { HasText = "dc-analytics" });
        await sourceChoice.Locator("input[type='radio']").CheckAsync();
        await page.GetByLabel("轉送元件名稱", new() { Exact = true }).FillAsync("otlp_http/source-a");
        await page.GetByRole(AriaRole.Button, new() { Name = "加入對應" }).ClickAsync();
        await page.GetByText("來源對應已加入。", new() { Exact = true }).WaitForAsync();
        Assert.Contains("otlp_http/source-a", await page.Locator(".route-list").InnerTextAsync());
        Assert.IsTrue(await sourceChoice.Locator("input[type='radio']").IsDisabledAsync());
        Assert.AreEqual("", await page.GetByLabel("轉送元件名稱", new() { Exact = true }).InputValueAsync());
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "collector-sources-desktop.png"), FullPage = true });
        await page.SetViewportSizeAsync(390, 844);
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "collector-sources-mobile.png"), FullPage = true });
        var collectorAdmin = scope.ServiceProvider.GetRequiredService<CollectorAdministration>();
        var collector = (await collectorAdmin.ListAsync(1, token)).Single();
        var firstRoute = (await collectorAdmin.RoutesAsync(collector.Id, token)).Single();
        var duplicateSource = await administration.IssueAsync("dc-analytics", Guid.CreateVersion7(), token);
        Assert.AreEqual(CollectorChangeStatus.Updated, await collectorAdmin.SetRouteAsync(collector.Id, duplicateSource.SourceId,
            collector.Version, "otlp_http/source-b", true, Guid.CreateVersion7(), token));
        var route = (await collectorAdmin.RoutesAsync(collector.Id, token)).Single(x => x.SourceId == firstRoute.SourceId);
        Guid instanceId;
        await using (var db = await metricFactory.CreateDbContextAsync(token))
            instanceId = await db.Instances.Where(x => x.ServiceId == monitoringServiceId && x.ExternalInstanceId == "collector-node-a").Select(x => x.Id).SingleAsync(token);
        var countersResponse = await page.GotoAsync(address + $"/settings/collectors/{collector.Id:D}/instances/{instanceId:D}/counters?source={route.SourceId:D}");
        Assert.IsNotNull(countersResponse);
        Assert.AreEqual(200, countersResponse.Status);
        var countersMain = await page.Locator("main").InnerTextAsync();
        Assert.Contains("選擇來源", countersMain, $"Counter page did not load the sources. Page: {countersMain}");
        var sourceLabels = await page.Locator(".source-identities").InnerTextAsync();
        Assert.Contains(firstRoute.SourceId.ToString("D"), sourceLabels);
        Assert.Contains(duplicateSource.SourceId.ToString("D"), sourceLabels);
        Assert.Contains("otlp_http/source-a", sourceLabels);
        Assert.Contains("otlp_http/source-b", sourceLabels);
        Assert.Contains("Log 已送出", await page.Locator("main").InnerTextAsync());
        Assert.Contains("比較期間失敗計數增加", await page.Locator("main").InnerTextAsync());
        Assert.Contains("送出失敗可能隨後重試", await page.Locator("main").InnerTextAsync());
        Assert.IsTrue(await page.GetByText("左右滑動表格，查看完整計數與時間。").IsVisibleAsync());
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
        var countersRegion = page.GetByRole(AriaRole.Region, new() { Name = "來源轉送計數，可左右滑動" });
        await countersRegion.FocusAsync();
        await countersRegion.PressAsync("ArrowRight");
        await page.WaitForFunctionAsync("() => document.querySelector('[aria-label=\"來源轉送計數，可左右滑動\"]').scrollLeft > 0");
        Assert.IsGreaterThan(0, await countersRegion.EvaluateAsync<int>("e => e.scrollLeft"));
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "collector-counters-mobile.png"), FullPage = true });
        await page.SetViewportSizeAsync(1440, 1024);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "collector-counters-desktop.png"), FullPage = true });
        await page.GotoAsync(address + $"/settings/collectors/{collector.Id:D}/sources");
        var selectedRoute = page.Locator(".route-list li").Filter(new() { HasText = firstRoute.SourceId.ToString("D") });
        await selectedRoute.GetByText("移除對應", new() { Exact = true }).ClickAsync();
        await selectedRoute.GetByLabel("我了解此操作不會變更 Collector 設定").CheckAsync();
        await selectedRoute.GetByRole(AriaRole.Button, new() { Name = "移除", Exact = true }).ClickAsync();
        await page.GetByText("來源對應已移除。", new() { Exact = true }).WaitForAsync();
        await page.GotoAsync(address + "/settings/audit?source=collectors");
        Assert.Contains("登記 Collector", await page.Locator("main").InnerTextAsync());
        Assert.Contains("加入來源對應", await page.Locator("main").InnerTextAsync());
        Assert.Contains("移除來源對應", await page.Locator("main").InnerTextAsync());
        context.AddResultFile(Path.Combine(evidence, "desktop.png"));
        context.AddResultFile(Path.Combine(evidence, "mobile.png"));

        var readyFile = Environment.GetEnvironmentVariable("VIGILOOM_RECEPTION_READY_FILE");
        if (!string.IsNullOrWhiteSpace(readyFile))
        {
            await File.WriteAllTextAsync(readyFile, address + "/settings/service-reception", token);
            for (var i = 0; i < 180 && !File.Exists(readyFile + ".done"); i++) await Task.Delay(500, token);
        }
    }, context.CancellationToken);
}
