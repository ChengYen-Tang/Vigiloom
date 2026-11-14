using System.Net;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Playwright;
using Vigiloom.Contracts.Publishing;
using Vigiloom.Modules.Resources.Domain;
using Vigiloom.Modules.Resources.Persistence;
using Vigiloom.Modules.ServiceObservability.Domain;
using Vigiloom.Modules.ServiceObservability.Domain.Health;
using Vigiloom.Modules.ServiceObservability.Persistence;
using Vigiloom.Web.Publishing;
using Vigiloom.Web.Tests.Fixtures;

namespace Vigiloom.Web.Tests;

[TestClass]
[TestCategory("Integration")]
[TestCategory("Database")]
[TestCategory("Browser")]
[DoNotParallelize]
public sealed class PublicStatusBrowserTests(TestContext context)
{
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task SlowHistoryAcrossRefresh_IsShownAndLaterQueriesUseCurrentTime() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        var clock = new TrackingClock(DateTimeOffset.UtcNow);
        var source = new SlowHistoryClient();
        using var original = PingTestSite.CreateFactory(database.ConnectionString);
        using var host = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPublicStatusClient>();
            services.AddScoped<IPublicStatusClient>(_ => source);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));
        host.UseKestrel(0); host.StartServer();
        var url = host.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var browserContext = await browser.NewContextAsync();
        var page = await browserContext.NewPageAsync();
        await using var render = await AutoRenderGate.HoldAsync(page);
        try
        {
            await page.GotoAsync(url);
            await source.FirstHistoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            await clock.RefreshTimerStarted.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            clock.Advance(TimeSpan.FromSeconds(15));
            await source.SecondRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            source.ReleaseFirstHistory.TrySetResult();
            var firstRow = page.Locator("li").Filter(new() { HasText = "First service" });
            await firstRow.Locator(".history-day").WaitForAsync(new() { Timeout = 15000 });
            await source.SecondHistoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            Assert.IsGreaterThan(source.FirstEnd, source.SecondEnd);
        }
        finally { source.ReleaseFirstHistory.TrySetResult(); }
    }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task LeavingDuringInitialLoad_CancelsAndWaitsForTheRead() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        var blocked = new BlockingStatusClient();
        using var original = PingTestSite.CreateFactory(database.ConnectionString);
        using var host = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IPublicStatusClient>();
            services.AddScoped<IPublicStatusClient>(_ => blocked);
        }));
        host.UseKestrel(0); host.StartServer();
        var url = host.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using (var client = new HttpClient { BaseAddress = new Uri(url) })
        using (var initial = await client.GetAsync("/", token))
        {
            Assert.AreEqual(HttpStatusCode.OK, initial.StatusCode);
            Assert.AreEqual(0, blocked.Calls, "Static prerender must not query public data.");
        }
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var browserContext = await browser.NewContextAsync();
        var page = await browserContext.NewPageAsync();
        await using var render = await AutoRenderGate.HoldAsync(page);
        try
        {
            await page.GotoAsync(url);
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), token);
            var navigation = page.GetByRole(AriaRole.Link, new() { Name = "登入", Exact = true }).ClickAsync();
            await blocked.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            blocked.Release.TrySetResult();
            await navigation.WaitAsync(TimeSpan.FromSeconds(15), token);
            await page.GetByRole(AriaRole.Heading, new() { Name = "登入 Vigiloom", Exact = true }).WaitForAsync();
            Assert.AreEqual(1, blocked.Calls);
        }
        finally { blocked.Release.TrySetResult(); }
    }, context.CancellationToken);

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public Task AnonymousPage_UsesPublicDataAndRemovesRevokedSelection() => ReusableWebDatabase.ExecuteAsync(async (database, token) =>
    {
        var clock = new TrackingClock(DateTimeOffset.UtcNow);
        var delay = new DelayedDeviceList();
        using var releaseOnCancellation = token.Register(() => delay.Release.TrySetResult());
        using var original = PingTestSite.CreateFactory(database.ConnectionString);
        using var host = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<PublicStreamAdmission>();
            services.AddSingleton(new PublicStreamAdmission(64, TimeSpan.FromMilliseconds(200), TimeSpan.FromMinutes(5), TimeProvider.System));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.RemoveAll<IPublicStatusClient>();
            services.AddScoped<IPublicStatusClient>(sp => new DelayedStatusClient(ActivatorUtilities.CreateInstance<ServerPublicStatusClient>(sp), delay));
        }));
        host.UseKestrel(0); host.StartServer();
        var privateDevice = await PingTestSite.InitializeAsync(host, token, schemaInitialized: true);
        var now = DateTimeOffset.UtcNow.AddMinutes(-3);
        var longName = new string('A', PublicationPolicy.MaximumNameLength);
        var device = new Device("private-router-identity", "10.9.8.7", true, now);
        device.SetPublication(true, "Public network", PublicationPolicy.BaselineVersion, now);
        var longDevice = new Device("long-device", "10.9.8.8", true, now);
        longDevice.SetPublication(true, longName, PublicationPolicy.BaselineVersion, now);
        var group = new Group("private-group-identity", now);
        group.SetPublication(true, "External group", PublicationPolicy.BaselineVersion, now);
        var longGroup = new Group("long-group", now);
        longGroup.SetPublication(true, longName, PublicationPolicy.BaselineVersion, now);
        var source = new ServiceSource("private-source", new byte[32], now);
        var candidate = new ServiceCandidate(source.Id, "private-key", "private-environment", "Private service", now);
        var service = new MonitoredService(candidate, now);
        service.SetPublication(true, "Public service", PublicationPolicy.ServiceHistoryDetailsVersion, now);
        service.ConfirmPublicSince(now);
        var longCandidate = new ServiceCandidate(source.Id, "long-key", "private-environment", "Long service", now);
        var longService = new MonitoredService(longCandidate, now);
        longService.SetPublication(true, longName, PublicationPolicy.BaselineVersion, now);
        var target = new HealthTarget(service.Id, null, "http://127.0.0.1/private-health", 30, 5, 3, 2, now);
        var interval = new HealthStateInterval(target.Id, 1, 1, now);
        interval.Close(now.AddMinutes(1));
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
        {
            db.Sources.Add(source); db.Candidates.Add(candidate); db.Services.Add(service);
            db.Candidates.Add(longCandidate); db.Services.Add(longService);
            db.HealthTargets.Add(target); db.HealthStateIntervals.Add(interval);
            await db.SaveChangesAsync(token);
        }
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
        {
            db.Devices.AddRange(device, longDevice); db.Groups.AddRange(group, longGroup);
            db.DeviceGroups.Add(new DeviceGroup(group.Id, device.Id));
            db.DeviceGroups.Add(new DeviceGroup(group.Id, privateDevice.Id));
            db.ServiceGroups.Add(new ServiceGroup(group.Id, service.Id));
            db.DeviceGroups.Add(new DeviceGroup(longGroup.Id, longDevice.Id));
            db.ServiceGroups.Add(new ServiceGroup(longGroup.Id, longService.Id));
            await db.SaveChangesAsync(token);
        }
        var url = host.ClientOptions.BaseAddress.ToString().TrimEnd('/');
        using (var anonymous = new HttpClient { BaseAddress = new Uri(url) })
        using (var initial = await anonymous.GetAsync("/", token))
        {
            Assert.AreEqual(HttpStatusCode.OK, initial.StatusCode);
            Assert.IsTrue(initial.Headers.CacheControl?.NoStore);
            var html = await initial.Content.ReadAsStringAsync(token);
            Assert.DoesNotContain("10.9.8.7", html);
            Assert.DoesNotContain("private-router-identity", html);
            Assert.DoesNotContain("private-key", html);
            Assert.DoesNotContain("private-health", html);
            using var alternate = await anonymous.GetAsync("/status/", token);
            Assert.AreEqual(HttpStatusCode.OK, alternate.StatusCode);
            Assert.IsTrue(alternate.Headers.CacheControl?.NoStore);
        }
        using (var reader = PingTestSite.Client(original))
        {
            foreach (var path in new[] { $"/devices/{device.Id:D}", $"/services/{service.Id:D}" })
            {
                using var denied = await reader.GetAsync(path, token);
                Assert.AreEqual(HttpStatusCode.Redirect, denied.StatusCode);
                Assert.Contains("/account/login", denied.Headers.Location?.ToString() ?? string.Empty);
            }
            await PingTestSite.LoginAsync(reader, "reader", token);
            foreach (var path in new[] { $"/devices/{device.Id:D}", $"/services/{service.Id:D}" })
            {
                using var allowed = await reader.GetAsync(path, token);
                Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
            }
        }

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var browserContext = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1280, Height = 900 } });
        var page = await browserContext.NewPageAsync();
        var streamOpened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodies = new ConcurrentBag<Task<string>>();
        var captureNetwork = false;
        page.Response += (_, response) =>
        {
            var path = new Uri(response.Url).AbsolutePath;
            if (path.EndsWith("/events", StringComparison.Ordinal) && response.Status == 200) streamOpened.TrySetResult();
        };
        page.RequestFinished += (_, request) =>
        {
            var path = new Uri(request.Url).AbsolutePath;
            if (captureNetwork && path.StartsWith("/api/v1/public/", StringComparison.Ordinal) && !path.EndsWith("/events", StringComparison.Ordinal))
                bodies.Add(ReadFinishedBodyAsync(request));
        };
        await using var render = await AutoRenderGate.HoldAsync(page);
        await page.GotoAsync(url);
        await page.Locator(".public-status[data-runtime='Server']").WaitForAsync(new() { Timeout = 60000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "External group", Exact = false }).WaitForAsync();
        Assert.Contains("1 個公開設備・1 個公開服務", await page.GetByRole(AriaRole.Button, new() { Name = "External group", Exact = false }).InnerTextAsync());
        Assert.AreEqual(0, await page.GetByText("private-router-identity").CountAsync());
        Assert.AreEqual(0, await page.GetByText("10.9.8.7").CountAsync());
        await page.SetViewportSizeAsync(390, 844);
        await page.GetByRole(AriaRole.Button, new() { Name = longName, Exact = true }).First.WaitForAsync();
        var overflows = await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth");
        Assert.IsFalse(overflows, "Long public names must not make the page scroll horizontally.");
        var longServiceRow = page.Locator(".public-row-heading").Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = longName, Exact = true }) });
        var stateBox = await longServiceRow.Locator(".public-state").BoundingBoxAsync();
        Assert.IsNotNull(stateBox);
        Assert.IsLessThan(40, stateBox.Height, "The status must stay readable beside a long service name.");
        await page.SetViewportSizeAsync(1280, 900);
        await page.GetByRole(AriaRole.Button, new() { Name = "External group", Exact = false }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Public network", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Public network", Exact = true }).WaitForAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "登入後查看設備詳情" })).ToHaveAttributeAsync("href", $"/devices/{device.Id:D}");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Public network", Exact = true })).ToHaveAttributeAsync("aria-expanded", "true");
        Assert.AreEqual(1, await page.Locator("li").Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = "Public network", Exact = true }) }).GetByRole(AriaRole.Heading, new() { Name = "Public network", Exact = true }).CountAsync());
        await streamOpened.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        await clock.RefreshTimerStarted.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        delay.Arm();
        try
        {
            clock.Advance(TimeSpan.FromSeconds(15));
            await delay.Captured.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ResourcesDbContext>>().CreateDbContextAsync(token))
            {
                (await db.Devices.SingleAsync(x => x.Id == device.Id, token)).SetPublication(false, null, null, DateTimeOffset.UtcNow);
                await db.SaveChangesAsync(token);
            }
            await page.GetByRole(AriaRole.Heading, new() { Name = "Public network", Exact = true }).WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 10000 });
            await page.GetByRole(AriaRole.Button, new() { Name = "Public network", Exact = true }).WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 10000 });
        }
        finally { delay.Release.TrySetResult(); }
        await delay.Refreshed.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "External group", Exact = false })).ToContainTextAsync("0 個公開設備・1 個公開服務");
        Assert.AreEqual(0, await page.GetByRole(AriaRole.Button, new() { Name = "Public network", Exact = true }).CountAsync());
        await render.ReleaseAsync();
        await AutoRenderGate.WaitForWebAssemblyCachedAsync(page);
        await page.ReloadAsync();
        await page.Locator(".public-status[data-runtime='WebAssembly']").WaitForAsync(new() { Timeout = 60000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Public service", Exact = true }).WaitForAsync();
        await page.Locator(".history-strip").WaitForAsync(new() { Timeout = 30000 });
        captureNetwork = true;
        var strip = await page.Locator(".history-strip").BoundingBoxAsync();
        var latestDay = await page.Locator(".history-day").Last.BoundingBoxAsync();
        Assert.IsNotNull(strip); Assert.IsNotNull(latestDay);
        Assert.IsGreaterThan(strip.X + strip.Width * .6, latestDay.X, "A recently published day belongs near the end of the selected seven-day window.");
        Assert.AreEqual(0, await page.GetByRole(AriaRole.Button, new() { Name = "上一頁", Exact = true }).CountAsync());
        await page.GetByLabel("搜尋公開名稱").FillAsync("private-key");
        await page.GetByRole(AriaRole.Button, new() { Name = "搜尋", Exact = true }).ClickAsync();
        await page.GetByText("目前沒有可顯示的公開服務。", new() { Exact = true }).WaitForAsync();
        await page.GetByLabel("搜尋公開名稱").FillAsync("");
        await page.GetByRole(AriaRole.Button, new() { Name = "搜尋", Exact = true }).ClickAsync();
        await page.Locator(".history-strip").WaitForAsync(new() { Timeout = 30000 });
        await page.GetByLabel("服務歷史").SelectOptionAsync("custom");
        await page.GetByLabel("開始時間（UTC）").FillAsync(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm"));
        await page.GetByLabel("結束時間（UTC）").FillAsync(DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-ddTHH:mm"));
        await page.GetByRole(AriaRole.Button, new() { Name = "套用時間範圍" }).ClickAsync();
        try { await page.GetByText("開始時間必須早於結束時間。", new() { Exact = true }).WaitForAsync(new() { Timeout = 5000 }); }
        catch (TimeoutException)
        {
            Assert.Fail($"Missing range validation. Fields: {await page.GetByLabel("開始時間（UTC）").InputValueAsync()} / {await page.GetByLabel("結束時間（UTC）").InputValueAsync()}. Text: {await page.Locator(".public-filters").InnerTextAsync()}");
        }
        await page.GetByLabel("開始時間（UTC）").FillAsync(DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-ddTHH:mm"));
        await page.GetByLabel("結束時間（UTC）").FillAsync(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm"));
        await page.GetByRole(AriaRole.Button, new() { Name = "套用時間範圍" }).ClickAsync();
        await page.Locator(".history-strip").WaitForAsync(new() { Timeout = 30000 });
        await page.Locator(".history-day").First.ClickAsync();
        await page.GetByRole(AriaRole.Heading, new() { Name = "Public service", Exact = true }).WaitForAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = "登入後查看服務詳情" })).ToHaveAttributeAsync("href", $"/services/{service.Id:D}");
        await page.GetByText("區間起點", new() { Exact = false }).First.WaitForAsync(new() { Timeout = 30000 });
        await using (var db = await host.Services.GetRequiredService<IDbContextFactory<ServiceObservabilityDbContext>>().CreateDbContextAsync(token))
        {
            (await db.Services.SingleAsync(x => x.Id == service.Id, token)).SetPublication(true, "Public service", PublicationPolicy.BaselineVersion, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync(token);
        }
        await page.GetByText("此服務目前未提供該時段的公開歷史。", new() { Exact = true }).WaitForAsync(new() { Timeout = 25000 });
        Assert.AreEqual(0, await page.GetByText("10.9.8.7").CountAsync());
        Assert.AreEqual(0, await page.GetByText("private-key").CountAsync());
        var responses = await Task.WhenAll(bodies.ToArray()).WaitAsync(TimeSpan.FromSeconds(10), token);
        Assert.IsNotEmpty(responses);
        foreach (var body in responses)
        {
            Assert.DoesNotContain("10.9.8.7", body);
            Assert.DoesNotContain("private-router-identity", body);
            Assert.DoesNotContain("private-key", body);
            Assert.DoesNotContain("private-health", body);
        }
    }, context.CancellationToken);

    private static async Task<string> ReadFinishedBodyAsync(IRequest request)
    {
        var response = await request.ResponseAsync();
        return response is null ? string.Empty : await response.TextAsync();
    }

    private sealed class DelayedDeviceList
    {
        private int state;
        public TaskCompletionSource Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Refreshed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Arm() => Volatile.Write(ref state, 1);
        public async Task<PublicPage<PublicDevice>> InterceptAsync(PublicPage<PublicDevice> result, CancellationToken token)
        {
            if (Interlocked.CompareExchange(ref state, 2, 1) == 1)
            {
                Captured.TrySetResult();
                await Release.Task.WaitAsync(token);
            }
            else if (Volatile.Read(ref state) == 2) Refreshed.TrySetResult();
            return result;
        }
    }

    private sealed class DelayedStatusClient(IPublicStatusClient inner, DelayedDeviceList delay) : IPublicStatusClient
    {
        public Task<PublicPage<PublicGroup>> GroupsAsync(string? search, int page, CancellationToken token) => inner.GroupsAsync(search, page, token);
        public Task<PublicGroup?> GroupAsync(Guid id, CancellationToken token) => inner.GroupAsync(id, token);
        public async Task<PublicPage<PublicDevice>> DevicesAsync(string? search, int page, Guid? groupId, CancellationToken token) =>
            await delay.InterceptAsync(await inner.DevicesAsync(search, page, groupId, token), token);
        public Task<PublicDevice?> DeviceAsync(Guid id, CancellationToken token) => inner.DeviceAsync(id, token);
        public Task<PublicPage<PublicService>> ServicesAsync(string? search, int page, Guid? groupId, CancellationToken token) => inner.ServicesAsync(search, page, groupId, token);
        public Task<PublicService?> ServiceAsync(Guid id, CancellationToken token) => inner.ServiceAsync(id, token);
        public Task<PublicDeviceHistory?> DeviceHistoryAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken token) => inner.DeviceHistoryAsync(id, from, to, token);
        public Task<PublicServiceHistory?> ServiceHistoryAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken token) => inner.ServiceHistoryAsync(id, from, to, token);
        public Task<PublicServiceHealthPeriods?> ServicePeriodsAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken token) => inner.ServicePeriodsAsync(id, from, to, token);
    }

    private sealed class BlockingStatusClient : IPublicStatusClient
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PublicPage<PublicGroup>> GroupsAsync(string? search, int page, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            using var registration = cancellationToken.Register(() => Canceled.TrySetResult());
            Entered.TrySetResult();
            await Release.Task;
            cancellationToken.ThrowIfCancellationRequested();
            return new([], page, false);
        }
        public Task<PublicGroup?> GroupAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<PublicGroup?>(null);
        public Task<PublicPage<PublicDevice>> DevicesAsync(string? search, int page, Guid? groupId, CancellationToken cancellationToken) => Task.FromResult(new PublicPage<PublicDevice>([], page, false));
        public Task<PublicDevice?> DeviceAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<PublicDevice?>(null);
        public Task<PublicPage<PublicService>> ServicesAsync(string? search, int page, Guid? groupId, CancellationToken cancellationToken) => Task.FromResult(new PublicPage<PublicService>([], page, false));
        public Task<PublicService?> ServiceAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<PublicService?>(null);
        public Task<PublicDeviceHistory?> DeviceHistoryAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) => Task.FromResult<PublicDeviceHistory?>(null);
        public Task<PublicServiceHistory?> ServiceHistoryAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) => Task.FromResult<PublicServiceHistory?>(null);
        public Task<PublicServiceHealthPeriods?> ServicePeriodsAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) => Task.FromResult<PublicServiceHealthPeriods?>(null);
    }

    private sealed class TrackingClock(DateTimeOffset start) : TimeProvider
    {
        private readonly FakeTimeProvider clock = new(start);
        public TaskCompletionSource RefreshTimerStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();
        public override long GetTimestamp() => clock.GetTimestamp();
        public override long TimestampFrequency => clock.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = clock.CreateTimer(callback, state, dueTime, period);
            if (period == TimeSpan.FromSeconds(15)) RefreshTimerStarted.TrySetResult();
            return timer;
        }
        public void Advance(TimeSpan amount) => clock.Advance(amount);
    }

    private sealed class SlowHistoryClient : IPublicStatusClient
    {
        private readonly PublicService first = new(Guid.CreateVersion7(), "First service", "Unknown");
        private readonly PublicService second = new(Guid.CreateVersion7(), "Second service", "Unknown");
        private int groupsCalls;
        public TaskCompletionSource FirstHistoryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondRefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstHistory { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondHistoryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset FirstEnd { get; private set; }
        public DateTimeOffset SecondEnd { get; private set; }
        public Task<PublicPage<PublicGroup>> GroupsAsync(string? search, int page, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref groupsCalls) == 2) SecondRefreshStarted.TrySetResult();
            return Task.FromResult(new PublicPage<PublicGroup>([], page, false));
        }
        public Task<PublicGroup?> GroupAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<PublicGroup?>(null);
        public Task<PublicPage<PublicDevice>> DevicesAsync(string? search, int page, Guid? groupId, CancellationToken cancellationToken) => Task.FromResult(new PublicPage<PublicDevice>([], page, false));
        public Task<PublicDevice?> DeviceAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<PublicDevice?>(null);
        public Task<PublicPage<PublicService>> ServicesAsync(string? search, int page, Guid? groupId, CancellationToken cancellationToken) => Task.FromResult(new PublicPage<PublicService>([first, second], page, false));
        public Task<PublicService?> ServiceAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<PublicService?>(null);
        public Task<PublicDeviceHistory?> DeviceHistoryAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) => Task.FromResult<PublicDeviceHistory?>(null);
        public async Task<PublicServiceHistory?> ServiceHistoryAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
        {
            if (id == first.Id)
            {
                FirstEnd = to; FirstHistoryStarted.TrySetResult();
                await ReleaseFirstHistory.Task.WaitAsync(cancellationToken);
            }
            else { SecondEnd = to; SecondHistoryStarted.TrySetResult(); }
            return new([new PublicHealthDay(to.AddMinutes(-1), to, "Unknown", null, 0)]);
        }
        public Task<PublicServiceHealthPeriods?> ServicePeriodsAsync(Guid id, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) => Task.FromResult<PublicServiceHealthPeriods?>(null);
    }
}
