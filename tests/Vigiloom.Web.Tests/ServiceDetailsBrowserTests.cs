using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Google.Protobuf;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Resource.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Trace.V1;
using Vigiloom.Modules.ServiceObservability.Application;
using Vigiloom.Modules.ServiceObservability.Application.Telemetry;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
[DoNotParallelize]
public sealed class ServiceDetailsBrowserTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Reader_CorrelatesTraceAndLog() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        factory.UseKestrel(0); factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        Guid serviceId, longServiceId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var source = await scope.ServiceProvider.GetRequiredService<SourceCredentials>().IssueAsync("correlation source", token);
            var discovery = scope.ServiceProvider.GetRequiredService<CandidateDiscovery>();
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(source.SourceId, "orders", "prod", "Orders", token));
            var candidate = (await discovery.ListAsync(1, token)).Single(x => x.SourceId == source.SourceId);
            var approved = await scope.ServiceProvider.GetRequiredService<ServiceApproval>().ApproveAsync(candidate.Id, Guid.CreateVersion7(), token);
            Assert.IsNotNull(approved.ServiceId); serviceId = approved.ServiceId.Value;
            var db = factory.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>();
            var resource = new Resource();
            resource.Attributes.Add(new KeyValue { Key = "service.name", Value = new AnyValue { StringValue = "orders" } });
            resource.Attributes.Add(new KeyValue { Key = "deployment.environment.name", Value = new AnyValue { StringValue = "prod" } });
            var id = ByteString.CopyFrom(Enumerable.Repeat((byte)1, 16).ToArray());
            var spanId = ByteString.CopyFrom(Enumerable.Repeat((byte)2, 8).ToArray());
            var time = (ulong)DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds() * 1_000_000;
            var logs = new ScopeLogs { LogRecords = { new LogRecord { TimeUnixNano = time, Body = new AnyValue { StringValue = "correlated log" }, TraceId = id, SpanId = spanId } } };
            Assert.AreEqual(LogIngestionStatus.Accepted, (await new LogIngestion(db).WriteAsync(source.SourceId, new ResourceLogs { Resource = resource, ScopeLogs = { logs } }, logs, token)).Status);
            var spans = new ScopeSpans { Spans = { new Span { Name = "order request", TraceId = id, SpanId = spanId, StartTimeUnixNano = time, EndTimeUnixNano = time } } };
            Assert.AreEqual(TraceIngestionStatus.Accepted, await new TraceIngestion(db).WriteAsync(source.SourceId, new ResourceSpans { Resource = resource, ScopeSpans = { spans } }, spans, token));
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(source.SourceId, "long-service", new string('e', 64), new string('W', 128), token));
            var longCandidate = (await discovery.ListAsync(1, token)).Single(x => x.ServiceKey == "long-service");
            var longService = await scope.ServiceProvider.GetRequiredService<ServiceApproval>().ApproveAsync(longCandidate.Id, Guid.CreateVersion7(), token);
            Assert.IsNotNull(longService.ServiceId); longServiceId = longService.ServiceId.Value;
            await using var resources = await factory.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token);
            var associated = new Group("Associated group", DateTimeOffset.UtcNow);
            var unrelated = new Group("Unrelated group", DateTimeOffset.UtcNow);
            resources.Groups.AddRange(associated, unrelated);
            resources.ServiceGroups.Add(new ServiceGroup(associated.Id, serviceId));
            for (var index = 0; index < 19; index++)
            {
                var extra = new Group($"Extra group {index:D2}", DateTimeOffset.UtcNow);
                resources.Groups.Add(extra);
                resources.ServiceGroups.Add(new ServiceGroup(extra.Id, serviceId));
            }
            var longName = new Group(new string('Z', 256), DateTimeOffset.UtcNow);
            resources.Groups.Add(longName);
            resources.ServiceGroups.Add(new ServiceGroup(longName.Id, serviceId));
            await resources.SaveChangesAsync(token);
        }
        var url = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{url}/services/{serviceId:D}");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("reader");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.Locator(".service-overview[data-interactive='true']").GetByRole(AriaRole.Heading, new() { Name = "Orders" }).WaitForAsync(new() { Timeout = 60000 });
        var groupLink = page.GetByRole(AriaRole.Link, new() { Name = "Associated group" });
        await groupLink.WaitForAsync();
        Assert.AreEqual(0, await page.GetByRole(AriaRole.Link, new() { Name = "Unrelated group" }).CountAsync());
        await groupLink.ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Associated group" }).WaitForAsync();
        await page.GotoAsync($"{url}/services/{serviceId:D}");
        await page.Locator(".service-overview[data-interactive='true']").GetByRole(AriaRole.Heading, new() { Name = "Orders" }).WaitForAsync(new() { Timeout = 60000 });
        await page.SetViewportSizeAsync(390, 844);
        await page.GetByRole(AriaRole.Navigation, new() { Name = "所屬群組分頁" }).GetByRole(AriaRole.Link, new() { Name = "下一頁" }).ClickAsync();
        await page.GetByRole(AriaRole.Link, new() { Name = new string('Z', 256) }).WaitForAsync();
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth"));
        await page.GetByRole(AriaRole.Navigation, new() { Name = "所屬群組分頁" }).GetByRole(AriaRole.Link, new() { Name = "上一頁" }).ClickAsync();
        await page.Locator(".service-overview[data-interactive='true']").GetByRole(AriaRole.Heading, new() { Name = "Orders" }).WaitForAsync(new() { Timeout = 60000 });
        await page.GetByRole(AriaRole.Link, new() { Name = "Associated group" }).WaitForAsync();
        Assert.AreEqual(0, await page.GetByRole(AriaRole.Link, new() { Name = new string('Z', 256) }).CountAsync());
        await page.GetByLabel("結束時間（UTC）").FillAsync(DateTimeOffset.UtcNow.AddMinutes(5).ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture));
        await page.GetByRole(AriaRole.Button, new() { Name = "查看 Traces" }).ClickAsync();
        await page.GetByText("order request", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "查看相關 Logs" }).ClickAsync();
        await page.GetByText("correlated log", new() { Exact = true }).WaitForAsync();
        await page.GetByText("來源時間", new() { Exact = true }).WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "清除篩選" }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "清除篩選" }).WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        Assert.AreEqual(0, await page.GetByRole(AriaRole.Button, new() { Name = "清除篩選" }).CountAsync());
        await page.GetByRole(AriaRole.Link, new() { Name = "查看接入導引" }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "應用服務接入導引" }).WaitForAsync();
        await page.GetByText("orders", new() { Exact = true }).WaitForAsync();
        Assert.AreEqual(0, await page.GetByRole(AriaRole.Link, new() { Name = "建立來源與管理待加入服務" }).CountAsync());
        Assert.AreEqual(0, await page.GetByRole(AriaRole.Link, new() { Name = "設定服務健康檢查" }).CountAsync());
        if (Environment.GetEnvironmentVariable("VIGILOOM_SERVICE_UI_EVIDENCE_DIR") is { Length: > 0 } guideEvidence)
        {
            Directory.CreateDirectory(guideEvidence);
            await page.SetViewportSizeAsync(1486, 1058);
            await page.ScreenshotAsync(new() { Path = Path.Combine(guideEvidence, "guide-desktop.png"), FullPage = true });
            await page.SetViewportSizeAsync(390, 844);
            await page.ScreenshotAsync(new() { Path = Path.Combine(guideEvidence, "guide-mobile.png"), FullPage = true });
        }
        await page.SetViewportSizeAsync(390, 844);
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth"));
        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync($"{url}/services/{longServiceId:D}");
        var longHeading = page.Locator(".service-overview[data-interactive='true'] .service-heading");
        await longHeading.WaitForAsync(new() { Timeout = 60000 });
        Assert.IsTrue(await longHeading.EvaluateAsync<bool>("element => element.scrollWidth <= element.clientWidth"));
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth"));
        await using var administrator = await browser.NewContextAsync();
        var adminPage = await administrator.NewPageAsync();
        await adminPage.GotoAsync($"{url}/services/connect?service={serviceId:D}");
        await adminPage.GetByLabel("帳號", new() { Exact = true }).FillAsync("admin");
        await adminPage.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await adminPage.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await adminPage.GetByRole(AriaRole.Link, new() { Name = "建立來源與管理待加入服務" }).WaitForAsync();
        Assert.AreEqual(1, await adminPage.GetByRole(AriaRole.Link, new() { Name = "設定服務健康檢查" }).CountAsync());
    }, TestContext.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task Reader_CanInspectServiceAndChangeRange() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        using var factory = PingTestSite.CreateFactory(database.ConnectionString);
        factory.UseKestrel(0);
        factory.StartServer();
        await PingTestSite.InitializeAsync(factory, token, schemaInitialized: true);
        Guid serviceId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var source = await scope.ServiceProvider.GetRequiredService<SourceCredentials>().IssueAsync("browser source", token);
            var discovery = scope.ServiceProvider.GetRequiredService<CandidateDiscovery>();
            Assert.AreEqual(CandidateObservation.Recorded, await discovery.ObserveAsync(source.SourceId, "orders", "prod", "Orders", token));
            var candidate = (await discovery.ListAsync(1, token)).Single(x => x.SourceId == source.SourceId);
            var approved = await scope.ServiceProvider.GetRequiredService<ServiceApproval>().ApproveAsync(candidate.Id, Guid.CreateVersion7(), token);
            Assert.AreEqual(ApprovalStatus.Approved, approved.Status);
            Assert.IsNotNull(approved.ServiceId);
            serviceId = approved.ServiceId.Value;
        }

        var url = factory.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 390, Height = 844 } });
        var page = await context.NewPageAsync();
        await using var wasmGate = await AutoRenderGate.HoldAsync(page);
        await page.GotoAsync($"{url}/services/{serviceId:D}");
        await page.GetByLabel("帳號", new() { Exact = true }).FillAsync("reader");
        await page.GetByLabel("密碼", new() { Exact = true }).FillAsync("Strong-Password-123");
        await page.GetByRole(AriaRole.Button, new() { Name = "登入", Exact = true }).ClickAsync();
        await page.Locator(".service-overview[data-runtime='Server'][data-interactive='true']").GetByRole(AriaRole.Heading, new() { Name = "Orders" }).WaitForAsync(new() { Timeout = 60000 });
        await page.GetByText("目前狀態：", new() { Exact = false }).WaitForAsync();
        await page.GetByText("尚未設定健康檢查", new() { Exact = true }).WaitForAsync();
        await page.GetByText("此服務尚未加入群組。", new() { Exact = true }).WaitForAsync();
        var unselected = page.GetByRole(AriaRole.Button, new() { Name = "最近 1 小時" });
        Assert.AreEqual("rgb(23, 66, 126)", await unselected.EvaluateAsync<string>("element => getComputedStyle(element).color"));
        Assert.AreEqual(0, await page.GetByRole(AriaRole.Link, new() { Name = "管理健康檢查" }).CountAsync());
        if (Environment.GetEnvironmentVariable("VIGILOOM_SERVICE_UI_EVIDENCE_DIR") is { Length: > 0 } evidence)
        {
            Directory.CreateDirectory(evidence);
            await page.SetViewportSizeAsync(1486, 1058);
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "service-desktop.png"), FullPage = true });
            await page.SetViewportSizeAsync(390, 844);
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "service-mobile.png"), FullPage = true });
        }
        await page.GetByRole(AriaRole.Button, new() { Name = "查看健康檢查依據" }).ClickAsync();
        await page.GetByText("此頁尚無健康檢查觀察。").WaitForAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "查看 Logs" }).ClickAsync();
        try { await page.GetByText("這段時間沒有日誌資料。").WaitForAsync(new() { Timeout = 10000 }); }
        catch (TimeoutException) { Assert.Fail(await page.Locator("body").InnerTextAsync()); }
        await page.GetByLabel("開始時間（UTC）").FillAsync("2030-01-02T00:00");
        await page.GetByLabel("結束時間（UTC）").FillAsync("2030-01-01T00:00");
        await page.GetByRole(AriaRole.Button, new() { Name = "查詢", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Alert).GetByText("開始須早於結束", new() { Exact = false }).WaitForAsync();
        await wasmGate.ReleaseAsync();
        await AutoRenderGate.WaitForWebAssemblyCachedAsync(page);
        await page.ReloadAsync();
        await page.Locator(".service-overview[data-runtime='WebAssembly'][data-interactive='true']").GetByRole(AriaRole.Heading, new() { Name = "Orders" }).WaitForAsync(new() { Timeout = 60000 });
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await page.RouteAsync("**/api/v1/service-details/*/logs?*", async route =>
        {
            requested.TrySetResult();
            await release.Task;
            try { await route.ContinueAsync(); } catch (PlaywrightException) { }
            finally { finished.TrySetResult(); }
        });
        try
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "查看 Logs" }).ClickAsync();
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            await page.GetByRole(AriaRole.Button, new() { Name = "查看 Traces" }).ClickAsync();
            await page.GetByText("這段時間沒有追蹤資料。").WaitForAsync();
        }
        finally { release.TrySetResult(); }
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        Assert.AreEqual(0, await page.GetByRole(AriaRole.Heading, new() { Name = "Logs（日誌）" }).CountAsync());
        Assert.AreEqual(1, await page.GetByRole(AriaRole.Heading, new() { Name = "Traces（追蹤）" }).CountAsync());
    }, TestContext.CancellationToken);
}
